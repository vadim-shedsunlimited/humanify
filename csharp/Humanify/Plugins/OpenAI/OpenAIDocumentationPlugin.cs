
using System.Collections.Concurrent;
using System.Text;
using Acornima;
using Acornima.Ast;
using Humanify.Ast;

namespace Humanify.Plugins.OpenAI;

using AstProgram = Acornima.Ast.Program;

public class OpenAIDocumentationPlugin : OpenAIBasePlugin {
    public override string Name => "OpenAI Documentation Plugin";
    public override string Identifier => "LLM_DOC";
    public override string Description => "Plugin for generating documentation using OpenAI's API.";

    public override double Weight => 2;

    public OpenAIDocumentationPlugin(List<OpenAIClient> clients, int contextWindowSize, string baseOutputDir)
        : base(clients, contextWindowSize, baseOutputDir) { }

    public override async Task<(string Code, AstProgram? Ast)> ProcessAsync(PluginContext context) {
        var input = context.Code;
        var fileName = context.FilePath;

        // Use shared AST
        var ast = context.GetAst();

        // Find targets (Functions, Classes, and their Exports)
        var visitor = new DocumentationVisitor();
        visitor.Visit(ast);
        var targets = visitor.Targets;

        if (targets.Count == 0) return (input, (AstProgram?)ast);

        // Filter out trivial or already documented items
        int initialCount = targets.Count;
        targets.RemoveAll(node => IsTrivial(node) || HasExistingDocumentation(input, node));

        if (targets.Count == 0) return (input, (AstProgram?)ast);

        // Map Node -> Generated Comment
        var commentMap = new ConcurrentDictionary<Node, string>();

        int systemPromptCost = TokenHelper.EstimateTokenCount(GetSystemPrompt());

        // Reserve significant space for the documentation output (it generates English text)
        const int RESPONSE_BUFFER = 2000;

        // Calculate space available for the Code + Prompt Overhead
        int availableForInput = _contextWindowSize - systemPromptCost - RESPONSE_BUFFER;

        if (availableForInput < 500) {
            Logger.LogWarning($"[{Identifier}] [{fileName}] Warning: Context window too small for documentation ({availableForInput}).");
            availableForInput = 500;
        }

        // Batching Strategy: Group targets until context window is full
        var batches = new List<List<Node>>();
        var currentBatch = new List<Node>();
        int currentBatchCost = 0;

        foreach (var node in targets) {
            string nodeCode = GetCodeForScope(input, node, systemPromptCost);

            // Estimate cost: Code + "--- ITEM X ---" overhead
            int cost = TokenHelper.EstimateTokenCount(nodeCode) + 20;

            // Check if adding this node would exceed the limit
            if (currentBatch.Count > 0 && (currentBatchCost + cost) > availableForInput) {
                batches.Add([.. currentBatch]);
                currentBatch.Clear();
                currentBatchCost = 0;
            }

            currentBatch.Add(node);
            currentBatchCost += cost;
        }
        if (currentBatch.Count > 0) batches.Add(currentBatch);

        Logger.LogInfo($"[{Identifier}] [{fileName}] Found {targets.Count} items to document. Processing in {batches.Count} batches.");

        // Process Batches
        var indexedBatches = batches.Select((b, i) => new { Data = b, Index = i + 1 });

        await Parallel.ForEachAsync(indexedBatches, new ParallelOptions { MaxDegreeOfParallelism = -1 }, async (item, ct) => {
            // Note: We don't wait on _semaphore here anymore because the OpenAIBasePlugin.CallApiWithRetries
            // handles concurrency per-provider internally. We just let the thread pool manage the Tasks.

            context.Progress.Report((double)item.Index / batches.Count);

            try {
                var batchNodes = item.Data;

                // Estimate output needs based on input item count
                int estimatedOutputTokens = Math.Max(1000, batchNodes.Count * 150);

                int safeMaxTokens = Math.Min(4096, estimatedOutputTokens);

                var response = await CallApiWithRetries(GetSystemPrompt(), GetUserPrompt(input, batchNodes), safeMaxTokens);
                var results = ParseJsonResponse(response);

                foreach (var kvp in results) {
                    if (int.TryParse(kvp.Key, out int index) && index >= 0 && index < batchNodes.Count) {
                        commentMap.TryAdd(batchNodes[index], kvp.Value);
                    }
                }

                Logger.LogInfo($"[{Identifier}] [{fileName}] Batch {item.Index} completed. Documented {results.Count} items.");

            } catch (Exception ex) {
                Logger.LogError($"[{Identifier}] [{fileName}] Batch {item.Index} failed: {ex.Message}");
            }
        });

        context.Progress.Report(0.99);

        // AST is invalidated by inserted comments
        return (ApplyComments(input, commentMap), null);
    }

    // Apply Comments (Insert before nodes)
    // Sort descending by position to keep indices valid
    private static string ApplyComments(string input, IDictionary<Node, string> commentMap) {
        var sortedComments = commentMap.OrderByDescending(kvp => kvp.Key.Range.Start).ToList();
        var sb = new StringBuilder(input);

        foreach (var kvp in sortedComments) {
            var node = kvp.Key;

            // Unescape the literal \n strings returned by JSON
            var comment = kvp.Value.Replace("\\n", "\n").Trim();

            // Standardize JSDoc wrap
            if (!comment.StartsWith("/**")) {
                comment = "/**\n * " + (comment.StartsWith("*") ? comment.TrimStart('*').Trim() : comment) + "\n */";
            } else {
                // Ensure it ends with */
                if (!comment.TrimEnd().EndsWith("*/")) {
                    comment = comment.TrimEnd() + "\n */";
                }
            }

            // Indentation Logic (Fix for invalid syntax)
            // Only capture whitespace. If characters are code, assume 0 indentation.
            int lineStart = input.LastIndexOf('\n', node.Range.Start) + 1;
            string potentialIndent = input[lineStart..node.Range.Start];

            string indentation = "";
            if (string.IsNullOrWhiteSpace(potentialIndent)) {
                indentation = potentialIndent;
            }

            // Apply indentation to every line of the comment
            var indentedComment = string.Join("\n" + indentation, comment.Split('\n'));

            // Newline Injection (Fix for "Newline issue above")
            // If the character before the insertion point is NOT a newline, prepend one.
            // This ensures comments don't start mid-line (e.g. "var a=1;/**...")
            if (node.Range.Start > 0) {
                char prevChar = input[node.Range.Start - 1];
                if (prevChar != '\n' && prevChar != '\r') {
                    indentedComment = "\n" + indentation + indentedComment;
                }
            }

            // Insert with appropriate spacing
            sb.Insert(node.Range.Start, indentedComment + "\n" + indentation);
        }

        return sb.ToString();
    }

    private static bool IsTrivial(Node node) {
        // Skip empty functions
        if (node is FunctionDeclaration fd) return fd.Body.Body.Count == 0;
        if (node is FunctionExpression fe) return fe.Body.Body.Count == 0;
        if (node is ArrowFunctionExpression af) {
            if (af.Body is BlockStatement bs) return bs.Body.Count == 0;
            return false; // Implicit return is not trivial
        }
        if (node is ClassDeclaration cd) return cd.Body.Body.Count == 0;
        return false;
    }

    private static bool HasExistingDocumentation(string input, Node node) {
        // Scan backwards from the start of the node to see if a JSDoc comment immediately precedes it.
        int pos = node.Range.Start - 1;

        // Skip whitespace
        while (pos >= 0 && char.IsWhiteSpace(input[pos])) pos--;

        // Check for end of comment '*/'
        if (pos >= 1 && input[pos] == '/' && input[pos - 1] == '*') {
            // It has a comment. Is it JSDoc (starts with /**)?
            // Scan backwards for the start token
            while (pos >= 1) {
                if (input[pos] == '*' && input[pos - 1] == '/') {
                    // Found start '/*' at pos-1. Check if next char is '*'
                    if (pos + 1 < input.Length && input[pos + 1] == '*') {
                        return true; // Found /**
                    }
                    return false; // Found /* (regular comment), so we can document it.
                }
                pos--;
            }
        }
        return false;
    }

    private static string GetSystemPrompt() {
        StringBuilder sb = new();
        sb.AppendLine("You are an expert JavaScript documentation generator.");
        sb.AppendLine("Generate concise JSDoc comments for the following functions/classes.");
        sb.AppendLine("Return a JSON object where keys are the indices (0, 1, 2...) and values are the comment strings (including /** ... */).");
        sb.AppendLine("Do not document getters/setters or empty constructors.");
        return sb.ToString();
    }

    private string GetUserPrompt(string input, List<Node> batchNodes) {
        var sbPrompt = new StringBuilder();

        sbPrompt.AppendLine("Code to document:");

        for (int i = 0; i < batchNodes.Count; i++) {
            sbPrompt.AppendLine($"\n--- ITEM {i} ---");
            sbPrompt.AppendLine(GetCodeForScope(input, batchNodes[i]));
        }

        return sbPrompt.ToString();
    }
}