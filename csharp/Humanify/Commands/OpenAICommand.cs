using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Threading.Tasks;
using Humanify.Configuration;
using Humanify.Plugins;
using Humanify.Plugins.Babel;
using Humanify.Plugins.OpenAI;
using Humanify.Plugins.Prettier;
using Humanify.Plugins.Validation;

namespace Humanify.Commands;

public static class OpenAICommand {
    public static Command Create() {
        var command = new Command("openai", "Use OpenAI's API to unminify code");

        var configOption = new Option<string>(
            name: "--config",
            description: "Path to a JSON configuration file");
        command.AddOption(configOption);

        var modelOption = new Option<string>(
            name: "--model",
            description: "The model to use",
            getDefaultValue: () => Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini");
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

        var parallelFilesOption = new Option<int>(
            name: "--parallelFiles",
            description: "Number of files to process in parallel",
            getDefaultValue: () => 1);
        command.AddOption(parallelFilesOption);

        var agentsOption = new Option<int>(
            name: "--agents",
            description: "Number of concurrent workers to process extracted modules",
            getDefaultValue: () => 1);
        command.AddOption(agentsOption);

        var apiKeyOption = new Option<string>(
            name: "--apiKey",
            description: "The OpenAI API key. Alternatively use OPENAI_API_KEY environment variable");
        command.AddOption(apiKeyOption);

        var baseURLOption = new Option<string>(
            name: "--baseURL",
            description: "The OpenAI base server URL",
            getDefaultValue: () => Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "https://api.openai.com/v1");
        command.AddOption(baseURLOption);

        var verboseOption = new Option<bool>(
            name: "--verbose",
            description: "Show verbose output");
        command.AddOption(verboseOption);

        var contextSizeOption = new Option<int>(
            name: "--contextSize",
            description: "The context size to use for the LLM",
            getDefaultValue: () => int.TryParse(Environment.GetEnvironmentVariable("OPENAI_CONTEXT_MAX_TOKENS"), out var val) ? val : 4000);
        command.AddOption(contextSizeOption);

        // rootDirectory is primarily useful in config, but adding as hidden CLI option could be useful too.
        // For now, we only read it from config as requested, but logic is handled below.

        var inputsArgument = new Argument<string[]>(
            name: "inputs",
            description: "One or more input minified Javascript files") {
            Arity = ArgumentArity.ZeroOrMore
        };
        command.AddArgument(inputsArgument);

        command.SetHandler(async (InvocationContext context) => {
            // Handle verbose logging
            Logger.ConsoleLevel = context.ParseResult.GetValueForOption(verboseOption) ? LogLevel.Debug : LogLevel.Info;

            var configPath = context.ParseResult.GetValueForOption(configOption);

            HumanifyConfig? config = null;
            if (!string.IsNullOrEmpty(configPath)) {
                if (File.Exists(configPath)) {
                    try {
                        var json = await File.ReadAllTextAsync(configPath);
                        config = JsonSerializer.Deserialize<HumanifyConfig>(json, new JsonSerializerOptions {
                            PropertyNameCaseInsensitive = true
                        });
                        if (config != null) {
                            Logger.LogInfo($"[Config] Loaded configuration from {configPath}");
                        }
                    } catch (Exception ex) {
                        Logger.LogError(ex, $"[Config] Failed to load configuration from {configPath}: {ex.Message}");
                        context.ExitCode = 1;
                        return;
                    }
                } else {
                    Logger.LogError($"[Config] Configuration file not found: {configPath}");
                    context.ExitCode = 1;
                    return;
                }
            }

            // --- RESOLVE SETTINGS ---
            // Resolve values: CLI (Explicit) > Config > CLI (Default/Env)
            var outputDir = ResolveSetting(context, outputDirOption, config?.OutputDir ?? null);
            var resume = ResolveSetting(context, resumeOption, config?.Resume ?? false);
            var continueOnError = ResolveSetting(context, continueOnErrorOption, config?.ContinueOnError ?? false);
            var filesInParallel = ResolveSetting(context, parallelFilesOption, config?.ParallelFiles ?? 1);
            var defaultAgents = ResolveSetting(context, agentsOption, config?.Agents ?? 1);
            var globalContextSize = ResolveSetting(context, contextSizeOption, config?.ContextSize ?? 4000);

            // Resolve API Key with Env Var fallback 
            var singleApiKey = ResolveSetting(context, apiKeyOption, config?.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
            var singleModel = ResolveSetting(context, modelOption, config?.Model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL"));
            var singleBaseUrl = ResolveSetting(context, baseURLOption, config?.BaseURL ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL"));

            // Inputs
            var inputs = context.ParseResult.GetValueForArgument(inputsArgument);
            if ((inputs == null || inputs.Length == 0) && config?.Inputs != null) {
                inputs = config.Inputs;
            }
            var rootDirectory = config?.RootDirectory;

            // --- BUILD CLIENT POOL ---
            var clients = new List<OpenAIClient>();

            // Add Providers from Config
            if (config?.Providers != null) {
                foreach (var p in config.Providers) {
                    if (string.IsNullOrEmpty(p.BaseUrl)) continue;

                    int providerContext = p.ContextSize ?? globalContextSize;

                    // We now route requests to providers that can handle context size
                    // if (providerContext < globalContextSize) {
                    //     globalContextSize = providerContext;
                    //     Logger.LogDebug($"[Init] Lowering global context size to {globalContextSize} based on provider {p.Name}");
                    // }

                    clients.Add(new OpenAIClient(
                        name: p.Name ?? "Provider",
                        apiKey: p.ApiKey ?? "",
                        baseURL: p.BaseUrl,
                        model: p.Model ?? "gpt-4o-mini",
                        contextSize: providerContext,
                        concurrency: p.MaxConcurrency ?? defaultAgents,
                        timeoutSeconds: p.TimeoutSeconds ?? 600,
                        chatEndpoint: p.ChatEndpoint ?? "chat/completions"
                    ));
                }
            }

            // Add Single Provider (CLI/Env/Legacy)
            // We verify if we should add the default/CLI provider
            bool isExplicitCli = context.ParseResult.FindResultFor(apiKeyOption)?.IsImplicit == false ||
                                 context.ParseResult.FindResultFor(baseURLOption)?.IsImplicit == false;

            if (clients.Count == 0 || isExplicitCli) {
                if (!string.IsNullOrEmpty(singleBaseUrl)) {
                    clients.Add(new OpenAIClient(
                        name: "Primary/CLI",
                        apiKey: singleApiKey ?? "", // Now resolved cleanly
                        baseURL: singleBaseUrl,
                        model: singleModel!,
                        contextSize: globalContextSize,
                        concurrency: defaultAgents
                    ));
                }
            }

            if (clients.Count == 0) {
                Logger.LogError("No AI Providers configured. Please check config file or CLI arguments.");
                context.ExitCode = 1;
                return;
            }

            Logger.LogInfo($"[Init] Initialized {clients.Count} AI Providers. Effective Batch Size: {globalContextSize} tokens");
            foreach (var c in clients) Logger.LogInfo($"   - {c.Name} ({c.Model}) [Threads: {c.Semaphore.CurrentCount}]");

            List<IPlugin> plugins = [
                new BabelPlugin(),
                new OpenAIRenamePlugin(clients, globalContextSize, outputDir!),
                new OpenAIDocumentationPlugin(clients, globalContextSize, outputDir!),
                new IntegrityPlugin(),
                new PrettierPlugin()
            ];

            var options = new UnminifyOptions(
                resume,
                continueOnError,
                defaultAgents,
                filesInParallel,
                $"multi-provider",
                rootDirectory
            );

            context.ExitCode = await Unminify.Execute(inputs!, outputDir!, plugins, options);
        });

        return command;
    }
    /// <summary>
    /// Resolves a setting with priority: 
    /// 1. CLI Explicit Flag
    /// 2. Config File Value
    /// 3. Passed Default Value (Optional)
    /// 4. CLI Default Value (Implicit)
    /// </summary>
    private static T ResolveSetting<T>(InvocationContext context, Option<T> option, T? configValue = default) {
        var result = context.ParseResult.FindResultFor(option);

        // If Explicitly set on CLI, use it.
        if (result != null && !result.IsImplicit) {
            return context.ParseResult.GetValueForOption(option)!;
        }

        // If Config has it, use it.
        if (configValue != null) {
            return configValue;
        }

        // Fallback to Option Default
        return context.ParseResult.GetValueForOption(option)!;
    }
}