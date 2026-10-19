namespace Humanify.Plugins;

using System.Collections.Concurrent;
using AstProgram = Acornima.Ast.Program;

public interface IPlugin {
    string Name { get; }
    string Description { get; }
    string Identifier => Name.Replace(" ", "_").ToLowerInvariant();

    string InstanceId { get; }

    /// <summary>
    /// Relative weight of this plugin for progress reporting.
    /// Default is 1.0. Heavier plugins (e.g. LLMs, Deobfuscators) should have higher values.
    /// </summary>
    double Weight => 1.0;

    /// <summary>
    /// Processes the input. Returns the new code and optionally a valid AST of that code.
    /// If AST is null, the pipeline will re-parse it lazily only if the next plugin needs it.
    /// </summary>
    Task<(string Code, AstProgram? Ast)> ProcessAsync(PluginContext context);

    /// <summary>
    /// Optional: Called when the pipeline rolls back past this plugin.
    /// Allows the plugin to clear any cached data for the specific file so it doesn't re-apply bad decisions.
    /// </summary>
    void InvalidateCache(string filePath, ConcurrentDictionary<string, string> pluginCache) {
        // Default: Do nothing
    }
}