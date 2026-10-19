using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace Humanify.Configuration;

public record HumanifyConfig(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("outputDir")] string? OutputDir,
    [property: JsonPropertyName("resume")] bool? Resume,
    [property: JsonPropertyName("continueOnError")] bool? ContinueOnError,
    [property: JsonPropertyName("agents")] int? Agents,
    [property: JsonPropertyName("parallelFiles")] int? ParallelFiles,
    [property: JsonPropertyName("apiKey")] string? ApiKey,
    [property: JsonPropertyName("baseURL")] string? BaseURL,
    [property: JsonPropertyName("contextSize")] int? ContextSize,
    // Multi-Provider Configuration
    [property: JsonPropertyName("providers")] List<LLMProviderConfig>? Providers,
    // File Inputs
    [property: JsonPropertyName("inputs")] string[]? Inputs,
    // Defines the root for calculating relative output paths
    [property: JsonPropertyName("rootDirectory")] string? RootDirectory
);

public record LLMProviderConfig(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("baseURL")] string? BaseUrl,
    [property: JsonPropertyName("apiKey")] string? ApiKey,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("contextSize")] int? ContextSize,
    [property: JsonPropertyName("maxConcurrency")] int? MaxConcurrency,
    [property: JsonPropertyName("timeoutSeconds")] int? TimeoutSeconds,
    [property: JsonPropertyName("chatEndpoint")] string? ChatEndpoint
);