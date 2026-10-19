using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;

namespace Humanify.Commands;

public static class DownloadCommand {
    public static Command Create() {
        var command = new Command("download", "Download a local LLM model");

        var modelOption = new Option<string>(
            name: "--model",
            description: "The model to download",
            getDefaultValue: () => "2b");
        command.AddOption(modelOption);

        var verboseOption = new Option<bool>(
            name: "--verbose",
            description: "Show verbose output");
        command.AddOption(verboseOption);

        command.SetHandler((InvocationContext context) => {
            var model = context.ParseResult.GetValueForOption(modelOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);

            if (verbose) {
                Logger.ConsoleLevel = LogLevel.Debug;
            } else {
                Logger.ConsoleLevel = LogLevel.Info;
            }

            Logger.LogInfo("Download command is not yet implemented in C# version.");
            Logger.LogInfo("This would download and set up local LLM models for inference.");
            Logger.LogInfo("For now, please use the TypeScript version for local model downloads.");

            context.ExitCode = 1;
        });

        return command;
    }
}
