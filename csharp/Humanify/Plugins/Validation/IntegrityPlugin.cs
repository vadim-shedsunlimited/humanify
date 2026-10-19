using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace Humanify.Plugins.Validation;

using AstProgram = Acornima.Ast.Program;

public class IntegrityPlugin : IPlugin {
    public string Name => "Integrity Check";
    public string Description => "Verifies code syntax and checks for LLM artifacts.";
    public string InstanceId { get; } = Guid.NewGuid().ToString();

    // Common phrases LLMs use when they fail or wrap code incorrectly
    private static readonly string[] _suspiciousPatterns = [
        "```",
        "Here is the code",
        "I apologize",
        "cannot rename",
        "[OUTPUT NOT AVAILABLE]"
    ];

    public Task<(string Code, AstProgram? Ast)> ProcessAsync(PluginContext context) {
        var code = context.Code;
        bool checksPassed = true;

        // Artifact Check
        foreach (var pattern in _suspiciousPatterns) {
            if (code.Contains(pattern, StringComparison.OrdinalIgnoreCase)) {
                Logger.LogWarning($"[{Name}] [{context.FilePath}] Potential LLM artifact detected: '{pattern}'");
                checksPassed = false;
            }
        }

        // Syntax Check
        // We use Tolerant=true to catch errors without throwing immediately, 
        // allowing us to filter out "Strict Mode" errors which are common in legacy bundles.
        // Use a custom error collector to capture errors without throwing immediately.
        var errors = new List<ParseError>();
        var parser = new Parser(new ParserOptions {
            Tolerant = true,
            EcmaVersion = EcmaVersion.Latest,
            ErrorHandler = new ErrorCollector(errors)
        });

        AstProgram? ast = null;
        try {
            // Try parsing as Module
            ast = parser.ParseModule(code);
        } catch {
            // Fallback to Script if Module fails hard (though Tolerant usually prevents throwing)
            try {
                ast = parser.ParseScript(code);
            } catch { }
        }

        // Analyze Collected Errors
        if (errors.Count > 0) {
            foreach (var error in errors) {
                // Allow Strict Mode violations (legacy bundles often mix 'arguments'/'eval' with ES6)
                if (error.Description.Contains("strict mode", StringComparison.OrdinalIgnoreCase)) {
                    Logger.LogWarning($"[{Name}] [{context.FilePath}] Strict Mode Violation (Allowed): {error.Description} at {error.LineNumber}:{error.Column}");
                }
                // Flag real syntax errors
                else {
                    Logger.LogError($"[{Name}] [{context.FilePath}] Syntax Error: {error.Description} at {error.LineNumber}:{error.Column}");
                    checksPassed = false;
                }
            }
        }

        if (!checksPassed) {
            // Throwing ensures Unminify.cs catches it and marks the Task as Failed
            throw new Exception($"Integrity checks failed. See logs for details.");
        }

        return Task.FromResult((code, ast));
    }

    // Helper class to capture Acornima errors
    private class ErrorCollector : ParseErrorHandler {
        private readonly List<ParseError> _errors;
        public ErrorCollector(List<ParseError> errors) {
            _errors = errors;
        }

        protected override void RecordError(ParseError error) {
            _errors.Add(error);
        }
    }
}