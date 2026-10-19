// #define TRACE_DEPENDENCY_RESOLUTION

using System.Collections.Concurrent;
using Acornima;
using Acornima.Ast;
using System.Text.RegularExpressions;

namespace Humanify;

using AstProgram = Acornima.Ast.Program;

public class DependencyGraph {
    // Stores AbsolutePath -> List of AbsolutePaths it imports
    public ConcurrentDictionary<string, HashSet<string>> AdjacencyList { get; } = new();

    // Stores AbsolutePath -> List of AbsolutePaths that import IT (Reverse graph for lookup)
    public ConcurrentDictionary<string, HashSet<string>> ReverseAdjacencyList { get; } = new();

    // Use OrdinalIgnoreCase to handle Windows/macOS path inconsistencies
    private readonly HashSet<string> _validFiles = new(StringComparer.OrdinalIgnoreCase);

    // Fast lookup for "Global Fallback" (FileName -> List of AbsolutePaths)
    private readonly Dictionary<string, List<string>> _fileNameIndex = new(StringComparer.OrdinalIgnoreCase);
    // FIX: Regex fallback for files that fail parsing (e.g. mixed ES/CommonJS syntax or duplicates)

    private static readonly Regex _requireRegex = new(@"require\s*\(\s*(['""])(?<path>.*?)\1\s*\)", RegexOptions.Compiled);
    private static readonly Regex _importFromRegex = new(@"(?:import|export)\s+(?:.+?\s+from\s+)?(['""])(?<path>.*?)\1", RegexOptions.Compiled);

    public async Task Build(IEnumerable<string> files) {
        var fileList = files.ToList();

        Logger.LogInfo($"[DependencyGraph] Normalizing {fileList.Count} input files...");

        // Normalize paths before adding to set
        foreach (var f in fileList) {
            // Normalize absolute path
            var full = Path.GetFullPath(f);
            _validFiles.Add(full);

            var fileName = Path.GetFileName(full);
            if (!_fileNameIndex.TryGetValue(fileName, out var list)) {
                list = new List<string>();
                _fileNameIndex[fileName] = list;
            }
            list.Add(full);
        }

#if TRACE_DEPENDENCY_RESOLUTION
        // DEBUG: Print first 3 files in the set to check formatting
        foreach (var f in _validFiles.Take(3)) Logger.LogDebug($"[Debug-Set] {f}");
#endif

        await Parallel.ForEachAsync(fileList, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (file, ct) => {
            // Normalize the key to Absolute Path immediately
            var fullFilePath = Path.GetFullPath(file);

            try {
                var code = await File.ReadAllTextAsync(fullFilePath, ct);
                var resolvedDependencies = new HashSet<string>();
                var fileDir = Path.GetDirectoryName(fullFilePath);
                var fileName = Path.GetFileName(fullFilePath);

                try {
                    // Robust Parsing (Module -> Script Fallback)
                    var parser = new Parser(new ParserOptions { Tolerant = true });
                    AstProgram ast;
                    try {
                        // Try parsing as Module (Strict Mode, supports 'import')
                        ast = parser.ParseModule(code);
                    } catch (SyntaxErrorException) {
                        // Fallback to Script (Sloppy Mode, supports 'var' redeclarations, handles 'require')
                        ast = parser.ParseScript(code);
                    }

                    var visitor = new ImportVisitor();
                    visitor.Visit(ast);


#if TRACE_DEPENDENCY_RESOLUTION
                    bool isTarget = fileName.Contains("26060.js");
                    if (isTarget) {
                        Logger.LogDebug($"[Target] Analyzing {fileName} | Raw Imports Found: {visitor.Imports.Count}");
                    }
#endif

                    foreach (var importPath in visitor.Imports) {
                        // Pass isTarget flag to ResolvePath to trace logic
                        var resolved = ResolvePath(
                            fileDir, importPath, fileName
#if TRACE_DEPENDENCY_RESOLUTION
                        , isTarget
#endif
                        );
                        if (resolved != null) {
                            resolvedDependencies.Add(resolved);
                        }
                    }

#if TRACE_DEPENDENCY_RESOLUTION
                    if (isTarget) {
                        Logger.LogDebug($"[Target] Final Dependencies: {resolvedDependencies.Count}");
                    }
#endif

                } catch {
                    // FALLBACK: AST Parsing failed (likely malformed source). Use Regex.
                    // This handles files with "export" (Module) AND duplicates (Script) which fail both parsers.
                    // Logger.LogDebug($"[DependencyGraph] Parsing failed for {fileName}, falling back to Regex: {ex.Message}");

                    var regexImports = new HashSet<string>();

                    // Scan require(...)
                    foreach (Match m in _requireRegex.Matches(code)) {
                        if (m.Success) regexImports.Add(m.Groups["path"].Value);
                    }

                    // Scan import/export ... from ...
                    // This is heuristic and might catch false positives in strings, but safer than dropping the file.
                    foreach (Match m in _importFromRegex.Matches(code)) {
                        if (m.Success) regexImports.Add(m.Groups["path"].Value);
                    }

                    foreach (var importPath in regexImports) {
                        var resolved = ResolvePath(fileDir, importPath, fileName);
                        if (resolved != null) resolvedDependencies.Add(resolved);
                    }

                    if (regexImports.Count == 0) {
                        // Only log error if we genuinely found nothing and parsing failed
                        Logger.LogWarning($"[DependencyGraph] Could not parse dependencies for {fileName} (Parser & Regex failed).");
                    }
                }


                // Store using the Absolute Path key
                AdjacencyList[fullFilePath] = resolvedDependencies;

                // Populate Reverse Lookup
                foreach (var dep in resolvedDependencies) {
                    ReverseAdjacencyList.AddOrUpdate(dep,
                        [fullFilePath], (key, existing) => {
                            lock (existing) {
                                existing.Add(fullFilePath);
                                return existing;
                            }
                        }
                    );
                }
            } catch (Exception ex) {
                // If both parses fail, log it but don't crash
                Logger.LogError(ex, $"[DependencyGraph] Failed to parse dependencies for {Path.GetFileName(file)}: {ex.Message}");
                AdjacencyList[fullFilePath] = [];
            }
        });
    }

    private string? ResolvePath(string? baseDir, string importPath, string sourceFileName, bool debugMode = false) {
        if (baseDir == null) return null;

        // Unpacked bundles often use IDs ("123") or flat paths ("utils") that map to sibling files.

        var candidates = new[] {
            importPath,
            importPath + ".js",
            importPath + "/index.js"
        };

        // Try Strict Relative/Sibling Resolution First       
        foreach (var c in candidates) {
            try {
                // This creates an Absolute Path
                var fullPath = Path.GetFullPath(Path.Combine(baseDir, c));

                // Check against our list of known Absolute Paths
                if (_validFiles.Contains(fullPath)) {
#if TRACE_DEPENDENCY_RESOLUTION
                    if (debugMode) {
                        Logger.LogDebug($"   [Trace] MATCH (Local): {importPath} -> {fullPath}");
                    }
#endif
                    return fullPath;
                }
            } catch { /* Ignore invalid paths */ }
        }

        // Global Fallback: Look for the file in ANY input folder
        // This handles cases where `require(123)` maps to `123.js` in a different bundle folder.
        foreach (var c in candidates) {
            // We strip any directory info from the import path to find the pure filename
            // e.g. "./utils/helper.js" -> "helper.js"
            // e.g. "123" -> "123" (which becomes "123.js" via candidates loop)
            var nameOnly = Path.GetFileName(c);

            if (_fileNameIndex.TryGetValue(nameOnly, out var matches)) {
                if (matches.Count == 1) {
                    var match = matches[0];
#if TRACE_DEPENDENCY_RESOLUTION
                    if (debugMode) {
                        Logger.LogDebug($"   [Trace] MATCH (Global): {importPath} -> {match}");
                    }
#endif
                    return match;
                } else if (matches.Count > 1) {
                    // Ambiguous match (e.g. 'index.js' appearing in multiple folders). 
                    // We skip this to avoid linking the wrong file.
#if TRACE_DEPENDENCY_RESOLUTION
                    if (debugMode) {
                        Logger.LogWarning($"   [Trace] AMBIGUOUS: '{importPath}' matches {matches.Count} files. Skipping.");
                    }
#endif
                }
            }
        }

#if TRACE_DEPENDENCY_RESOLUTION
        if (debugMode) Logger.LogDebug($"   [Trace] FAIL: {importPath}");
#endif
        return null;
    }

    public List<List<string>> GetExecutionLayers() {
        var layers = new List<List<string>>();

        // Calculate initial in-degrees (number of dependencies a file has)
        // Keys are now guaranteed to be Absolute Paths
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var remainingNodes = new HashSet<string>(AdjacencyList.Keys, StringComparer.OrdinalIgnoreCase);

        // Calculate initial in-degrees
        foreach (var node in remainingNodes) {
            int deg = 0;
            if (AdjacencyList.TryGetValue(node, out var deps)) {
                // Only count dependencies that are actually in our file list
                // Both 'node' and 'd' are absolute paths, so this intersection works
                deg = deps.Count(d => remainingNodes.Contains(d));
            }
            inDegree[node] = deg;
        }

        while (remainingNodes.Count > 0) {
            // Find all nodes with 0 dependencies (leaves)
            var currentLayer = remainingNodes.Where(n => inDegree[n] == 0).ToList();

            if (currentLayer.Count == 0) {
                // CYCLE DETECTED: Break it by picking the node with the fewest remaining dependencies
                // This minimizes the impact of missing context.
                var forcedNode = remainingNodes.OrderBy(n => inDegree[n]).First();
                Logger.LogDebug($"[Cycle Detected] Breaking cycle at {Path.GetFileName(forcedNode)}");
                currentLayer.Add(forcedNode);
            }

            layers.Add(currentLayer);

            // "Execute" this layer: remove it and update neighbors
            foreach (var node in currentLayer) {
                remainingNodes.Remove(node);

                // For every file that imported 'node', reduce its dependency count
                if (ReverseAdjacencyList.TryGetValue(node, out var dependents)) {
                    foreach (var dep in dependents) {
                        if (inDegree.ContainsKey(dep)) {
                            inDegree[dep]--;
                        }
                    }
                }
            }
        }

        return layers;
    }

    /// <summary>
    /// Logs the full dependency tree to Debug.
    /// Useful for auditing how files are connected.
    /// </summary>
    public void PrintGraph(string rootDir) {
        // Optimization: Don't calculate strings if we aren't logging
        if (Logger.ConsoleLevel > LogLevel.Debug && Logger.FileLevel > LogLevel.Debug) return;

        Logger.LogDebug("\n=== Dependency Graph ===");

        // Order by filename for readable logs
        foreach (var kvp in AdjacencyList.OrderBy(k => k.Key)) {
            var file = Path.GetRelativePath(rootDir, kvp.Key);

            // Only log if it actually imports something (reduces noise for leaf nodes)
            if (kvp.Value.Count > 0) {
                var deps = kvp.Value
                    .Select(d => Path.GetRelativePath(rootDir, d))
                    .OrderBy(d => d);

                Logger.LogDebug($"[{file}] imports: {string.Join(", ", deps)}");
            }
        }
        Logger.LogDebug("========================\n");
    }

    /// <summary>
    /// Logs the dependency graph as a visual tree structure (Debug level only).
    /// </summary>
    public void PrintDependencyTree(string rootDir, int maxDepth = 25) {
        // Optimization: Don't calculate strings if we aren't logging
        if (Logger.ConsoleLevel > LogLevel.Debug && Logger.FileLevel > LogLevel.Debug) return;

        Logger.LogDebug("\n=== Dependency Tree ===");
        Logger.LogDebug($"[Config] Max Depth: {maxDepth}");

        // 1. Identify Roots
        var roots = AdjacencyList.Keys
            .Where(k => !ReverseAdjacencyList.ContainsKey(k))
            .OrderBy(k => k)
            .ToList();

        if (roots.Count == 0) {
            roots = AdjacencyList.Keys.OrderBy(k => k).ToList();
        }

        var globalVisited = new HashSet<string>();

        foreach (var root in roots) {
            if (globalVisited.Contains(root)) continue;
            // Start recursion at depth 0
            PrintNode(root, "", true, globalVisited, new HashSet<string>(), rootDir, 0, maxDepth);
        }

        Logger.LogDebug("=======================\n");
    }

    private void PrintNode(
        string node, 
        string indent, 
        bool isLast, 
        HashSet<string> globalVisited, 
        HashSet<string> currentPath, 
        string rootDir,
        int currentDepth,
        int maxDepth
    ) {
        var relName = Path.GetRelativePath(rootDir, node);
        var marker = isLast ? "└── " : "├── ";

        // 1. Cycle Check
        if (currentPath.Contains(node)) {
             Logger.LogDebug($"{indent}{marker}{relName} (circular ref)");
             return;
        }

        // 2. Global Deduplication
        if (globalVisited.Contains(node)) {
            Logger.LogDebug($"{indent}{marker}{relName} *");
            return;
        }

        // 3. Print Node
        Logger.LogDebug($"{indent}{marker}{relName}");
        globalVisited.Add(node);

        // 4. Check Depth Limit
        if (currentDepth >= maxDepth) {
             // If there are children we are skipping, indicate it
             if (AdjacencyList.TryGetValue(node, out var childCheck) && childCheck.Count > 0) {
                 var nextIndent = indent + (isLast ? "    " : "│   ");
                 Logger.LogDebug($"{nextIndent}└── ... (depth limit reached)");
             }
             return;
        }

        // 5. Recurse
        currentPath.Add(node);
        if (AdjacencyList.TryGetValue(node, out var children) && children.Count > 0) {
            var sortedChildren = children.OrderBy(c => c).ToList();
            var newIndent = indent + (isLast ? "    " : "│   ");

            for (int i = 0; i < sortedChildren.Count; i++) {
                PrintNode(
                    sortedChildren[i], 
                    newIndent, 
                    i == sortedChildren.Count - 1, 
                    globalVisited, 
                    currentPath, 
                    rootDir,
                    currentDepth + 1, // Increment depth
                    maxDepth
                );
            }
        }
        currentPath.Remove(node);
    }

    private class ImportVisitor : AstVisitor {
        public HashSet<string> Imports { get; } = [];

        // import ... from "path"
        protected override object? VisitImportDeclaration(ImportDeclaration node) {
            if (node.Source is Literal lit && lit.Value is string s) Imports.Add(s);
            return base.VisitImportDeclaration(node);
        }

        // export ... from "path"
        protected override object? VisitExportNamedDeclaration(ExportNamedDeclaration node) {
            if (node.Source is Literal lit && lit.Value is string s) Imports.Add(s);
            return base.VisitExportNamedDeclaration(node);
        }

        protected override object? VisitExportAllDeclaration(ExportAllDeclaration node) {
            if (node.Source is Literal lit && lit.Value is string s) Imports.Add(s);
            return base.VisitExportAllDeclaration(node);
        }

        // require("path") OR require(123)
        protected override object? VisitCallExpression(CallExpression node) {
            if (node.Callee is Identifier id && id.Name == "require" && node.Arguments.Count > 0) {
                var arg = node.Arguments[0];
                if (arg is Literal lit) {
                    if (lit.Value is string s) {
                        Imports.Add(s);
                    }
                    // FIX: Handle numeric module IDs (common in Webpack bundles)
                    else if (lit.Value is double d) {
                        Imports.Add(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    } else if (lit.Value is int i) {
                        Imports.Add(i.ToString());
                    }
                }
            }
            return base.VisitCallExpression(node);
        }
    }
}