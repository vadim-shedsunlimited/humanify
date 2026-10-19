using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Acornima;
using Acornima.Ast;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Humanify.Plugins.Webcrack;

public static class Webcrack {
    public static async Task Prepare(IEnumerable<string> sourceFiles, string inputDir) {
        foreach (var sourceFile in sourceFiles) {
            await ProcessFile(sourceFile, inputDir);
        }
    }
    public static async Task ProcessFile(string sourceFile, string inputDir) {
        var fileName = Path.GetFileName(sourceFile);
        var bundleName = Path.GetFileNameWithoutExtension(sourceFile);

        // Output to a subfolder to keep things organized (and avoid collisions)
        var outputSubDir = Path.Combine(inputDir, bundleName);
        Directory.CreateDirectory(outputSubDir);

        Logger.LogInfo($"[Webcrack] Processing {fileName} via npm webcrack...");

        try {
            // We use 'npx webcrack' to run it without requiring global install.
            // --force overwrites existing files if we are re-running
            var args = $"webcrack \"{sourceFile}\" -o \"{outputSubDir}\" --force";

            var startInfo = new ProcessStartInfo {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd" : "npx",
                Arguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"/c npx {args}" : args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            var output = new List<string>();
            var error = new List<string>();

            process.OutputDataReceived += (s, e) => { if (e.Data != null) output.Add(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.Add(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0) {
                Logger.LogInfo($"[Webcrack] Successfully unpacked {fileName}");
            } else {
                Logger.LogError($"[Webcrack] Failed to unpack {fileName} (Exit Code {process.ExitCode}).");
                foreach (var line in error) Logger.LogError($"[Webcrack Error] {line}");

                // Fallback: Copy original so the pipeline can at least try to process the raw file
                await CopyOriginal(sourceFile, inputDir);
            }
        } catch (Exception ex) {
            Logger.LogError(ex, $"[Webcrack] Error executing webcrack: {ex.Message}. Is Node.js/npx installed?");
            await CopyOriginal(sourceFile, inputDir);
        }
    }

    private static async Task CopyOriginal(string sourceFile, string inputDir) {
        var fileName = Path.GetFileName(sourceFile);
        var destPath = Path.Combine(inputDir, fileName);
        if (!File.Exists(destPath)) {
            // Run on a thread pool thread to satisfy async requirements without blocking
            await Task.Run(() => File.Copy(sourceFile, destPath, true));
            Logger.LogInfo($"[Setup] Copied {fileName} to input directory (fallback)");
        }
    }
}