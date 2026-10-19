using System.Collections.Concurrent;
using Acornima;

namespace Humanify.Plugins;

using AstProgram = Acornima.Ast.Program;

public class PluginContext {
    public string FilePath { get; }
    public string Code { get; }
    public ConcurrentDictionary<string, string> SharedFileCache { get; }

    /// <summary>
    /// The Blackboard: A dictionary where keys are Plugin Identifiers, 
    /// and values are that plugin's private cache (Key -> Value).
    /// </summary>
    public ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Blackboard { get; }

    /// <summary>
    /// Reporter for normalized progress (0.0 to 1.0).
    /// </summary>
    public IProgress<double> Progress { get; }

    private AstProgram? _ast;

    public PluginContext(
        string filePath,
        string code,
        AstProgram? cachedAst = null,
        ConcurrentDictionary<string, string>? sharedCache = null,
        ConcurrentDictionary<string, ConcurrentDictionary<string, string>>? blackboard = null,
        IProgress<double>? progress = null
    ) {
        FilePath = filePath;
        Code = code;
        _ast = cachedAst;
        SharedFileCache = sharedCache ?? new ConcurrentDictionary<string, string>();
        Blackboard = blackboard ?? new ConcurrentDictionary<string, ConcurrentDictionary<string, string>>();
        Progress = progress ?? new Progress<double>(); // No-op fallback
    }

    /// <summary>
    /// Helper to get the private cache for a specific plugin.
    /// </summary>
    public ConcurrentDictionary<string, string> GetPluginCache(IPlugin plugin) {
        // Key Format: "openai_documentation_plugin:a1b2-c3d4..."
        string key = $"{plugin.Identifier}:{plugin.InstanceId}";
        return Blackboard.GetOrAdd(key, _ => new ConcurrentDictionary<string, string>());
    }

    /// <summary>
    /// Returns the cached AST or parses the Code on-demand using robust fallback logic.
    /// </summary>
    public AstProgram GetAst() {
        if (_ast != null) return _ast;

        var parser = new Parser(new ParserOptions {
            Tolerant = true,
            EcmaVersion = EcmaVersion.Latest
        });

        try {
            // Try parsing as Module (Strict Mode, supports 'import')
            _ast = parser.ParseModule(Code);
        } catch (SyntaxErrorException) {
            // Fallback to Script (Sloppy Mode, supports 'var' redeclarations, handles 'require')
            _ast = parser.ParseScript(Code);
        }

        return _ast;
    }

    /// <summary>
    /// Checks if the AST has already been materialized (useful for determining if we can reuse it).
    /// </summary>
    public bool HasCachedAst => _ast != null;
}