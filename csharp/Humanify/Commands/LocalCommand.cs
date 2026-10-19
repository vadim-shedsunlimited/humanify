using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;
using Humanify.Plugins.Babel;

namespace Humanify.Commands;

public static class LocalCommand {
    public static Command Create() {
        var command = new Command("local", "Use a local LLM to unminify code");

        var modelOption = new Option<string>(
            name: "--model",
            description: "The model to use",
            getDefaultValue: () => "llama-2-7b");
        command.AddOption(modelOption);

        var outputDirOption = new Option<string>(
            name: "--outputDir",
            description: "The output directory",
            getDefaultValue: () => "output");
        command.AddOption(outputDirOption);

        var resumeOption = new Option<bool>(
            name: "--resume",
            description: "Resume processing an existing --outputDir (skip re-extracting the bundle)");
        command.AddOption(resumeOption);

        var continueOnErrorOption = new Option<bool>(
            name: "--continueOnError",
            description: "Continue processing remaining files even if one fails");
        command.AddOption(continueOnErrorOption);

        var agentsOption = new Option<int>(
            name: "--agents",
            description: "Number of concurrent workers to process extracted modules",
            getDefaultValue: () => 1);
        command.AddOption(agentsOption);

        var seedOption = new Option<string?>(
            name: "--seed",
            description: "Seed for the model to get reproduceable results (leave out for random seed)");
        command.AddOption(seedOption);

        var disableGpuOption = new Option<bool>(
            name: "--disableGpu",
            description: "Disable GPU acceleration");
        command.AddOption(disableGpuOption);

        var verboseOption = new Option<bool>(
            name: "--verbose",
            description: "Show verbose output");
        command.AddOption(verboseOption);

        var contextSizeOption = new Option<int>(
            name: "--contextSize",
            description: "The context size to use for the LLM",
            getDefaultValue: () => 4000);
        command.AddOption(contextSizeOption);

        var inputsArgument = new Argument<string[]>(
            name: "inputs",
            description: "One or more input minified Javascript files") {
            Arity = ArgumentArity.ZeroOrMore
        };
        command.AddArgument(inputsArgument);

        command.SetHandler((InvocationContext context) => {
            var model = context.ParseResult.GetValueForOption(modelOption);
            var outputDir = context.ParseResult.GetValueForOption(outputDirOption);
            var resume = context.ParseResult.GetValueForOption(resumeOption);
            var continueOnError = context.ParseResult.GetValueForOption(continueOnErrorOption);
            var agents = context.ParseResult.GetValueForOption(agentsOption);
            var seed = context.ParseResult.GetValueForOption(seedOption);
            var disableGpu = context.ParseResult.GetValueForOption(disableGpuOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var contextSize = context.ParseResult.GetValueForOption(contextSizeOption);
            var inputs = context.ParseResult.GetValueForArgument(inputsArgument);

            if (verbose) {
                Logger.ConsoleLevel = LogLevel.Debug;
            } else {
                Logger.ConsoleLevel = LogLevel.Info;
            }

            Logger.LogInfo("Local inference mode is not yet implemented in C# version.");
            Logger.LogInfo("This would require integrating with a local LLM library like llama.cpp or similar.");
            Logger.LogInfo("For now, please use the TypeScript version for local inference.");

            context.ExitCode = 1;
        });

        return command;
    }
}
