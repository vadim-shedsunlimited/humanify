namespace Humanify.Plugins.OpenAI;

public interface ILlmClient {
    string Name { get; }
    bool IsRateLimited { get; }
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens);
}