
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;
using Humanify.Ast;

namespace Humanify.Plugins.OpenAI;

using AstProgram = Acornima.Ast.Program;

public partial class OpenAIRenamePlugin : OpenAIBasePlugin {
    public override string Name => "OpenAI Rename Plugin";
    public override string Identifier => "LLM_RENAME";
    public override string Description => "Renames minified/obfuscated JavaScript variables using OpenAI.";

    public override double Weight => 10;

    private const int BATCH_SIZE_LIMIT = 30;

    private static readonly HashSet<string> _reservedKeywords = new() {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do",
        "else", "export", "extends", "false", "finally", "for", "function", "if", "import", "in",
        "instanceof", "new", "null", "return", "super", "switch", "this", "throw", "true", "try",
        "typeof", "var", "void", "while", "with", "yield", "let", "static", "enum", "await", "implements",
        "interface", "package", "private", "protected", "public"
    };

    public OpenAIRenamePlugin(
        List<OpenAIClient> clients,
        int contextWindowSize,
        string baseOutputDir
    ) : base(clients, contextWindowSize, baseOutputDir) { }

    public override void InvalidateCache(string filePath, ConcurrentDictionary<string, string> pluginCache) {
        // The cache keys for this plugin are format: $"{fileName}::{minStart}::{name}"
        // We find and remove all keys starting with this file path.

        // Note: Identify the prefix used in ProcessAsync
        string prefix = $"{filePath}::";

        foreach (var key in pluginCache.Keys) {
            if (key.StartsWith(prefix)) {
                pluginCache.TryRemove(key, out _);
            }
        }

        Logger.LogDebug($"[{Identifier}] Invalidated cache for '{filePath}' due to rollback.");
    }

    public override async Task<(string Code, AstProgram? Ast)> ProcessAsync(PluginContext context) {
        var input = context.Code;
        var fileName = context.FilePath;

        // Access the Blackboard Cache for this plugin
        // We use this to store and retrieve renaming decisions to save tokens on re-runs
        var cache = context.GetPluginCache(this);

        // Use the shared AST from the context!
        // This skips parsing if Babel (or another plugin) already did it.
        var ast = context.GetAst();

        // Scan for Imports (require / import)
        // This maps variables (e.g. "r") to file paths (e.g. "./utils.js")
        var importScanner = new ImportScanner();
        importScanner.Visit(ast);
        var fileMap = importScanner.ImportMap;

        // Track Scopes
        // Pass imported identifiers to ScopeTrackingVisitor so we capture 'importedModule.minifiedName' usages
        var visitor = new ScopeTrackingVisitor {
            TrackPropertiesOf = fileMap.Keys.ToHashSet()
        };
        visitor.Visit(ast);

        if (visitor.ScopedIdentifiers.Count == 0) {
            // No changes, we can return the existing AST safely
            return (input, (AstProgram?)ast);
        }

        // Group by Scope first
        var scopes = visitor.ScopedIdentifiers
            .GroupBy(x => x.ScopeNode)
            .OrderBy(g => g.Key.Range.Start) // Process top-down
            .ToList();

        // Thread-safe collection to store successful renames
        var replacementMap = new ConcurrentDictionary<Identifier, string>();

        int systemPromptCost = TokenHelper.EstimateTokenCount(GetSystemPrompt());

        // Reserve tokens for the model's JSON response (Renaming lists can be long)
        const int RESPONSE_BUFFER = 1000;

        // Define the max "Input" tokens we can afford
        int maxTotalTokens = _contextWindowSize - systemPromptCost - RESPONSE_BUFFER;

        // Safety floor
        if (maxTotalTokens < 1000) {
            Logger.LogWarning($"[{Identifier}] [{fileName}] Warning: Context window is very tight ({maxTotalTokens} tokens available). Shortening context.");
            maxTotalTokens = 1000;
        }

        // Create Batches
        // We group identifiers that appear within the same 'Context Window' 
        // to ensure we can send one slice of code for all of them.
        var batches = new List<(Node Scope, List<string> Names, List<List<Identifier>> NodeGroups)>();

        foreach (var scopeGroup in scopes) {
            var scopeNode = scopeGroup.Key;

            // Pre-calculate variable bounds for efficient batching
            var variablesInScope = scopeGroup
                .GroupBy(x => x.Node.Name)
                .Select(g => {
                    var nodes = g.Select(x => x.Node).ToList();
                    return new {
                        Name = g.Key,
                        Nodes = nodes,
                        // Calculate the bounding box of this specific variable's usages
                        MinStart = nodes.Min(n => n.Range.Start),
                        MaxEnd = nodes.Max(n => n.Range.End)
                    };
                })
                .ToList();

            // Batch State
            var currentBatchNames = new List<string>();
            var currentBatchNodes = new List<List<Identifier>>();
            var currentBatchImports = new HashSet<string>();

            // We track the geometric bounds of the current batch to estimate the Context Slice cost
            int currentBatchMin = int.MaxValue;
            int currentBatchMax = int.MinValue;

            int currentBatchVarsCost = 0; // Only the tokens for the identifier list + imports

            foreach (var variable in variablesInScope) {
                if (!IsLikelyMinified(variable.Name)) continue;

                // [BLACKBOARD READ]
                // Check if we already have a rename for this specific variable instance in the cache.
                // Key format: "{FilePath}::{StartOffset}::{OriginalName}"
                string cacheKey = $"{fileName}::{variable.MinStart}::{variable.Name}";
                if (cache.TryGetValue(cacheKey, out var cachedName)) {
                    // Apply immediately and SKIP adding to the LLM batch
                    foreach (var node in variable.Nodes) {
                        replacementMap.TryAdd(node, cachedName);
                    }
                    Logger.LogDebug($"[{Identifier}] [{fileName}] using cached rename: '{variable.Name}' -> '{cachedName}'");
                    continue;
                }

                // Calculate cost of adding this variable to the LIST (Prompt: "- a\n")
                int varNameCost = TokenHelper.EstimateTokenCount(variable.Name) + 5;

                // Calculate Imports cost
                int importCost = 0;
                if (fileMap.TryGetValue(variable.Name, out var importPath) && !currentBatchImports.Contains(importPath)) {
                    // Check import cost (using Summary!)
                    var fullContent = await LoadFileContent(context, fileName, importPath);
                    var summary = GetModuleSummary(fullContent);
                    importCost = TokenHelper.EstimateTokenCount(summary);
                }

                // Calculate "Context Slice" Cost if we merge this variable
                //The slice needs to cover the existing batch range AND the new variable's range
                int tentativeMin = Math.Min(currentBatchMin, variable.MinStart);
                int tentativeMax = Math.Max(currentBatchMax, variable.MaxEnd);

                // Add buffer (before and after)
                int sliceLength = (tentativeMax - tentativeMin) + (2 * CONTEXT_CHARS_BUFFER);

                // Clamp to actual function size (we can't slice more than exists)
                int scopeLength = scopeNode.Range.End - scopeNode.Range.Start;
                if (sliceLength > scopeLength) sliceLength = scopeLength;

                // Estimate tokens for the code slice (conservative 3 chars/token)
                int estimatedContextCost = sliceLength / 3;

                // Decision: Does it fit?
                int projectedTotal = currentBatchVarsCost + varNameCost + importCost + estimatedContextCost;

                bool countFull = currentBatchNames.Count >= BATCH_SIZE_LIMIT;

                // If we have items and adding this one breaks the limit, FLUSH.
                if (currentBatchNames.Count > 0 && (projectedTotal > maxTotalTokens || countFull)) {
                    batches.Add((scopeNode, new List<string>(currentBatchNames), new List<List<Identifier>>(currentBatchNodes)));

                    // Reset
                    currentBatchNames.Clear();
                    currentBatchNodes.Clear();
                    currentBatchImports.Clear();
                    currentBatchMin = int.MaxValue;
                    currentBatchMax = int.MinValue;
                    currentBatchVarsCost = 0;
                }

                // Add to batch
                currentBatchNames.Add(variable.Name);
                currentBatchNodes.Add(variable.Nodes);
                if (importPath != null) currentBatchImports.Add(importPath);

                currentBatchVarsCost += varNameCost + importCost;

                // Update bounds
                if (variable.MinStart < currentBatchMin) currentBatchMin = variable.MinStart;
                if (variable.MaxEnd > currentBatchMax) currentBatchMax = variable.MaxEnd;
            }

            if (currentBatchNames.Count > 0) {
                batches.Add((scopeNode, currentBatchNames, currentBatchNodes));
            }
        }

        // Process Batches in Parallel
        var indexedBatches = batches.Select((b, i) => new { Data = b, Index = i + 1 });
        int totalBatches = batches.Count;

        // Create a way to track names taken in each scope
        // We calculate this by mapping locally defined names, then walking up the scope parent map
        // to ensure we include all visible variables (shadowing check).

        // Get locally defined names for every scope
        var definedNamesInScope = visitor.ScopedIdentifiers
            .GroupBy(x => x.ScopeNode)
            .ToDictionary(
                g => g.Key,
                g => new HashSet<string>(g.Select(x => x.Node.Name))
            );

        // Build the full "taken" set (Local + Ancestors)
        var takenNamesInScope = new Dictionary<Node, HashSet<string>>();

        foreach (var scopeNode in definedNamesInScope.Keys) {
            var visibleNames = new HashSet<string>();
            var current = scopeNode;

            // Walk up the scope chain
            while (current != null) {
                // Add variables defined in 'current' scope
                if (definedNamesInScope.TryGetValue(current, out var locals)) {
                    visibleNames.UnionWith(locals);
                }

                // Move to parent
                if (!visitor.ScopeParentMap.TryGetValue(current, out var parent)) {
                    break;
                }
                current = parent;
            }
            takenNamesInScope[scopeNode] = visibleNames;
        }

        Logger.LogInfo($"[{Identifier}] [{fileName}] Found {scopes.Count} scopes, {fileMap.Count} imports, {batches.Count} rename batches.");

        int totalRenames = 0;

        // Process Batches in Parallel
        await Parallel.ForEachAsync(indexedBatches, new ParallelOptions { MaxDegreeOfParallelism = -1 }, async (item, ct) => {
            // Note: We don't wait on _semaphore here anymore because the OpenAIBasePlugin.CallApiWithRetries
            // handles concurrency per-provider internally. We just let the thread pool manage the Tasks.

            try {
                // Determine context covering ALL nodes in this batch
                var (scopeNode, names, nodeGroups) = item.Data;

                context.Progress.Report((double)item.Index / totalBatches);

                Logger.LogDebug($"[{Identifier}] [{fileName}] ({item.Index}/{totalBatches}) Processing batch with {names.Count} variables...");

                // Generate the Code Scope FIRST so we can scan it
                var allFocusNodes = nodeGroups.SelectMany(g => g).Cast<Node>().ToList();
                string contextualCode = GetContextualScope(input, scopeNode, allFocusNodes);

                // Identify referenced modules (Imports)
                // We want to include summaries for:
                //  Imports that are being renamed (in 'names')
                //  Imports that are USED in the code scope (even if not renamed)
                var usedImports = new HashSet<(string Var, string Path)>();

                // Check Renamed vars
                foreach (var name in names) {
                    if (fileMap.TryGetValue(name, out var path)) usedImports.Add((name, path));
                }

                // Check Visible imports appearing in the code
                foreach (var kvp in fileMap) {
                    string importVar = kvp.Key;
                    string importPath = kvp.Value;

                    // Simple regex to check if the identifier appears in the code snippet as a whole word
                    // This is a heuristic but much faster than re-parsing the snippet
                    if (Regex.IsMatch(contextualCode, $@"\b{Regex.Escape(importVar)}\b")) {
                        usedImports.Add((importVar, importPath));
                    }
                }

                // Context Assembly
                var sbContext = new StringBuilder();

                // Add Summaries for identified imports
                foreach (var import in usedImports.DistinctBy(x => x.Path)) {
                    var content = await LoadFileContent(context, fileName, import.Path);
                    if (!string.IsNullOrWhiteSpace(content)) {
                        string summary = GetModuleSummary(content);
                        sbContext.AppendLine($"// MODULE: {import.Var} (from {import.Path})");
                        sbContext.AppendLine("// (Signatures only)");
                        sbContext.AppendLine(summary);
                        sbContext.AppendLine("// END MODULE\n");
                    }
                }

                // Add Scope Code
                sbContext.AppendLine("// TARGET CODE SCOPE:");

                // Construct Prompts
                string systemPrompt = GetSystemPrompt();
                string userPrompt = GetUserPrompt(names, sbContext.ToString(), takenNamesInScope[scopeNode]);

                var renames = ParseJsonResponse(await CallApiWithRetries(systemPrompt, userPrompt, 100 + (names.Count * 20)));
                var failedRenames = new List<string>();
                int appliedRenames = 0;

                void ApplyRenames(Dictionary<string, string> suggestions) {
                    foreach (var (oldName, proposedName) in suggestions) {
                        string newName = proposedName;

                        // If the model suggests a reserved keyword (e.g., 'if', 'char'), append '_var' instead of skipping.
                        if (_reservedKeywords.Contains(newName)) {
                            // Logger.LogWarning($"[{Identifier}] [{fileName}] ({item.Index}/{totalBatches}) Skipped applying invalid rename '{oldName}' -> '{newName}' (Reserved Keyword)");
                            newName += "_var";
                        }

                        // Validate it's a valid identifier (basic regex check)
                        if (!ValidIdentifierRegex().IsMatch(newName)) {
                            // Mark for retry if we haven't hit the limit yet
                            // Note: We handle the decision to retry in the loop below, 
                            // but here we just collect the failures from the current pass.
                            failedRenames.Add(oldName);
                            // Logger.LogWarning($"[{Identifier}] [{fileName}] ({item.Index}/{totalBatches}) Skipped applying invalid rename '{oldName}' -> '{newName}' (Invalid Syntax)");
                            continue;
                        }

                        // Scope Collision
                        // If 'newName' already exists in this scope, try to make it unique (e.g., newName_2)
                        var currentScopeTaken = takenNamesInScope[scopeNode];

                        // Register the name as "taken" so other batches in the same scope don't grab it
                        // Moved the check logic INSIDE the lock to prevent race conditions between batches
                        lock (currentScopeTaken) {
                            int suffix = 2;
                            string candidateName = newName;

                            // Check availability atomically
                            while (currentScopeTaken.Contains(candidateName)) {
                                candidateName = $"{newName}_{suffix++}";
                            }
                            newName = candidateName;

                            // Register the name as "taken"
                            currentScopeTaken.Add(newName);
                        }
                        Logger.LogDebug($"[{Identifier}] [{fileName}] ({item.Index}/{totalBatches}) Registered new name '{newName}' for old name '{oldName}' in scope {scopeNode.Type}");


                        // Apply to nodes
                        int idx = names.IndexOf(oldName);
                        if (idx != -1) {
                            // [BLACKBOARD WRITE]
                            // Store the successful rename in the cache
                            var nodes = nodeGroups[idx];
                            // We use the start of the first node as the anchor
                            if (nodes.Count > 0) {
                                int minStart = nodes.Min(n => n.Range.Start);
                                string cacheKey = $"{fileName}::{minStart}::{oldName}";
                                cache.TryAdd(cacheKey, newName);
                            }

                            foreach (var node in nodeGroups[idx]) {
                                replacementMap.TryAdd(node, newName);
                                appliedRenames++;
                            }
                            Logger.LogDebug($"[{Identifier}] [{fileName}] ({item.Index}/{totalBatches}) Renamed '{oldName}' -> '{newName}' in scope {scopeNode.Type}");
                        }
                    }
                }

                // Initial Pass
                ApplyRenames(renames);

                // Retry Loop
                int retryCount = 0;
                const int MAX_RETRIES = 5;

                while (failedRenames.Count > 0 && retryCount < MAX_RETRIES) {
                    retryCount++;
                    Logger.LogInfo($"[{Identifier}] [{fileName}] ({item.Index}/{totalBatches}) Retry {retryCount}/{MAX_RETRIES} for {failedRenames.Count} invalid names...");

                    // Create a prompt focusing ONLY on the failed names
                    string retryUserPrompt = GetUserPrompt(failedRenames, sbContext.ToString(), takenNamesInScope[scopeNode]);

                    // Clear failed list to see if they pass this time
                    var retryingNames = new List<string>(failedRenames);
                    failedRenames.Clear();

                    var retrySuggestions = ParseJsonResponse(await CallApiWithRetries(systemPrompt, retryUserPrompt, 100 + (retryingNames.Count * 20)));

                    // Apply (will populate failedRenames again if they fail again)
                    ApplyRenames(retrySuggestions);
                }

                if (failedRenames.Count > 0) {
                    Logger.LogWarning($"[{Identifier}] [{fileName}] ({item.Index}/{totalBatches}) Failed to rename the following {failedRenames.Count} variables after {MAX_RETRIES} retries: {string.Join(", ", failedRenames)}");
                }

                Logger.LogDebug($"[{Identifier}] [{fileName}] Batch {item.Index} completed. Applied {appliedRenames} | Failed {failedRenames.Count}");
                totalRenames += appliedRenames;
            } catch (Exception ex) {
                Logger.LogError(ex, $"[{Identifier}] [{fileName}] ({item.Index}/{totalBatches}) Batch {item.Index} failed: {ex.Message}");
            }
        });

        Logger.LogInfo($"[{Identifier}] [{fileName}] Total renames applied: {totalRenames}");

        context.Progress.Report(0.99);

        // Return new code. AST is now NULL because we invalidated it by changing string lengths.
        return (ApplyReplacements(input, replacementMap), null);
    }

    private static string ApplyReplacements(string input, IDictionary<Identifier, string> replacementMap) {
        // 4. Apply Replacements (Positional)
        // Sort descending by start position to avoid shifting indices
        var sortedReplacements = replacementMap
            .OrderByDescending(kvp => kvp.Key.Range.Start)
            .ToList();

        var sb = new StringBuilder(input);
        foreach (var kvp in sortedReplacements) {
            var node = kvp.Key;
            var newName = kvp.Value;

            // Safety check: ensure range is valid
            if (node.Range.End <= sb.Length) {
                sb.Remove(node.Range.Start, node.Range.End - node.Range.Start);
                sb.Insert(node.Range.Start, newName);
            }
        }
        return sb.ToString();
    }

    private static bool IsLikelyMinified(string name) {
        // 1 or 2 characters are always suspicious
        if (name.Length <= 2) return true;

        // Starts with underscore followed by alphanumeric (e.g., _0x1234, _a)
        if (name.StartsWith("_")) return true;

        // Format: Letter followed immediately by numbers (e.g., a1, b05)
        // This avoids renaming valid words like "foo2" or "item1" if we are conservative,
        // but often minifiers output "t0", "e1".
        // Regex: Single letter + digits
        if (char.IsLetter(name[0]) && name.Length > 1 && name.Skip(1).All(char.IsDigit)) return true;

        return false;
    }

    private static string GetSystemPrompt() {
        StringBuilder sb = new();
        sb.AppendLine("You are a code deobfuscation tool.");
        sb.AppendLine("Rename the provided minified identifiers based on context found in the provided JavaScript scope (Function/Class).");
        sb.AppendLine("The context may include SUMMARIES of imported modules (function signatures only). Use them to infer what variables (like 'r', 'o') represent.");
        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("1. **Context:** Use the provided code scope to infer the purpose of variables.");
        sb.AppendLine("2. **Generics:** If a variable is a generic iterator (i, j, k) or temp variable, LEAVE IT ALONE or name it `index`, `temp`.");
        sb.AppendLine("3. **Safety:** If you cannot determine a good name, omit it from the JSON.");
        sb.AppendLine("4. **Consistency:** Do not change valid names (e.g. `this`, `window`).");
        sb.Append($"5. **No Reserved Keywords:** Do NOT rename variables to JavaScript reserved keywords (e.g., ");
        foreach (var kv in _reservedKeywords) {
            sb.Append($"'{kv}', ");
        }
        sb.Append(", etc.).").AppendLine();
        sb.AppendLine("6. **No Dot Notation:** Do not use dot notation (e.g. `Math.max`) for variable names. If renaming a variable that aliases a global, use camelCase (e.g. `mathMax` or `nativeMax`).");
        sb.AppendLine();
        sb.AppendLine("Respond ONLY with a JSON object mapping the original name to the new name: {{ \"minifiedName\": \"SuggestName\", \"a\": \"index\" }}");
        return sb.ToString();
    }

    private static string GetUserPrompt(List<string> identifiers, string surroundingCode, IEnumerable<string> takenNames) {
        StringBuilder sb = new();
        sb.AppendLine("Identifiers to rename:");
        foreach (var id in identifiers) {
            sb.AppendLine($"- {id}");
        }
        sb.AppendLine();

        // Inform the AI of existing names it should avoid
        if (takenNames.Any()) {
            sb.AppendLine("The following names are ALREADY USED in this scope. DO NOT use them for new names:");
            sb.AppendLine(string.Join(", ", takenNames.Take(50))); // Cap at 50 to save tokens
            sb.AppendLine();
        }

        sb.AppendLine("Code Scope:");
        sb.AppendLine("```javascript");
        sb.AppendLine(surroundingCode);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string GetModuleSummary(string code) {
        if (string.IsNullOrWhiteSpace(code)) return "";
        try {
            var comments = new List<Comment>();
            // Use ParserOptions to capture comments
            var parser = new Parser(new ParserOptions { OnComment = (in Comment comment) => comments.Add(comment) });
            var ast = parser.ParseScript(code);

            // Pass the AST (Root) to the summarizer
            var summarizer = new ModuleSummarizer(code, ast, comments);
            summarizer.Visit(ast);
            return summarizer.GetSummary();
        } catch {
            // Fallback: Return first 500 chars if parsing fails
            return code.Length > 500 ? code.Substring(0, 500) + "..." : code;
        }
    }

    [GeneratedRegex(@"^[a-zA-Z_$][a-zA-Z0-9_$]*$")]
    private static partial Regex ValidIdentifierRegex();
}
