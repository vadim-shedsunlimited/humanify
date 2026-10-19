using System;
using System.Text;
using System.Threading.Tasks;
using Acornima;
using Acornima.Ast;
using Acornima.Jsx;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Humanify.Plugins.Prettier;

using AstProgram = Acornima.Ast.Program;

public class PrettierPlugin : IPlugin {
    public string Name => "Prettier Plugin";
    public string Description => "Formats JavaScript code using Prettier.";
    public string Identifier => "PRETTIER";
    public string InstanceId { get; } = Guid.NewGuid().ToString();

    public async Task<(string Code, AstProgram? Ast)> ProcessAsync(PluginContext context) {
        // Prettier invalidates AST because it changes whitespace/formatting (Source Locations change)
        var formattedCode = await Format(context.Code, context.FilePath);
        return (formattedCode, null);
    }

    private static async Task<string> Format(string code, string fileName = "unknown") {
        if (string.IsNullOrWhiteSpace(code)) return code;

        try {
            // Use 'npx prettier' via stdin to avoid temp files
            // We specify --stdin-filepath to help Prettier infer parser (though usually defaults to js)

            string args;

            // Windows CMD does not strip single quotes. We must use double quotes.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                // Escape double quotes in the filename just in case
                var escapedFileName = fileName.Replace("\"", "\\\"");
                args = $"prettier --stdin-filepath \"{escapedFileName}\"";
            } else {
                // Linux/Mac shells strip single quotes, which is safer for special chars ($)
                args = $"prettier --stdin-filepath '{fileName}'";
            }

            var startInfo = new ProcessStartInfo {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "npx",
                Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"/c npx {args}" : args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Force UTF-8 to handle Unicode characters (À, Ĳ, etc.) correctly
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Write code to stdin
            await process.StandardInput.WriteAsync(code);
            process.StandardInput.Close();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0) {
                // Trim the final newline that StringBuilder/Prettier adds
                return outputBuilder.ToString().TrimEnd();
            } else {
                throw new Exception($"[Prettier] Formatting failed (Exit {process.ExitCode}): {errorBuilder}");
            }
        } catch (Exception ex) {
            throw new Exception($"[Prettier] Error launching npx: {ex.Message}", ex);
        }
    }
}