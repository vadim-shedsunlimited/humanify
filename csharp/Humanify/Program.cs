using System.CommandLine;
using Humanify.Commands;
using DotNetEnv;
using Humanify.UI;

namespace Humanify;

class Program {
    static async Task<int> Main(string[] args) {
        Env.Load();

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"humanify_{timestamp}.log";
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".humanify", fileName);

        IUserInterface ui = new SpectreConsoleUI();

        Logger.Initialize(logPath, ui);
        Logger.LogInfo("Global logging initialized at: " + logPath);

        var rootCommand = new RootCommand("Unminify code using OpenAI's API or a local LLM") {
            Name = "humanify",
            Description = "Deobfuscate Javascript code using LLMs"
        };

        // Add subcommands
        rootCommand.AddCommand(OpenAICommand.Create());
        // rootCommand.AddCommand(LocalCommand.Create());
        // rootCommand.AddCommand(DownloadCommand.Create());

        // Set default command if no arguments provided
        if (args.Length == 0) {
            return await rootCommand.InvokeAsync("--help");
        }

        return await rootCommand.InvokeAsync(args);
    }
}
