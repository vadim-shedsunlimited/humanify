using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Humanify.Plugins.OpenAI;

/// <summary>
/// Represents a single AI Provider connection with its own rate limits and configuration.
/// </summary>
public class OpenAIClient : ILlmClient {
    public string Name { get; }
    public string BaseUrl { get; }
    public string ChatEndpoint { get; }
    public string ApiKey { get; }
    public string Model { get; }

    /// <summary>
    /// The maximum number of tokens this specific model can handle (Input + Output).
    /// </summary>
    public int ContextSize { get; }

    // Concurrency Control
    public SemaphoreSlim Semaphore { get; }

    // Timeout for this specific provider
    public TimeSpan Timeout { get; }

    // Rate Limiting State
    public DateTimeOffset RateLimitedUntil { get; private set; } = DateTimeOffset.MinValue;
    public bool IsRateLimited => DateTimeOffset.UtcNow < RateLimitedUntil;

    private static readonly HttpClient _httpClient = new() {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    public OpenAIClient(
        string name,
        string apiKey,
        string baseURL,
        string model,
        int contextSize,
        int concurrency = 1,
        int timeoutSeconds = 600,
        string chatEndpoint = "chat/completions"
    ) {
        Name = name;
        ApiKey = apiKey;
        ChatEndpoint = chatEndpoint;
        BaseUrl = baseURL.TrimEnd('/');
        Model = model;
        ContextSize = contextSize;

        // Ensure at least 1 slot allowed
        int slots = concurrency > 0 ? concurrency : 1;
        Semaphore = new SemaphoreSlim(slots, slots);

        // Default to 600s (10 mins) if not specified
        Timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 600);
    }

    /// <summary>
    /// Sends a chat completion request to this specific provider.
    /// </summary>
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens) {
        // Fast fail if we already know we are rate limited (double-check)
        if (IsRateLimited) {
            var wait = RateLimitedUntil - DateTimeOffset.UtcNow;
            throw new HttpRequestException($"[{Name}] Client is rate-limited. Available in {wait.TotalSeconds:F0}s", null, HttpStatusCode.TooManyRequests);
        }

        var requestBody = new {
            model = Model,
            messages = new[] {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.1,
            max_tokens = maxTokens,
            stream = false // Ensure single JSON response
        };

        var jsonContent = JsonSerializer.Serialize(requestBody);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{ChatEndpoint}");
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(ApiKey)) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        }

        // We use a cancellation token for the specific request timeout logic
        using var cts = new CancellationTokenSource(Timeout);

        try {
            // Use ResponseHeadersRead for efficiency
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!response.IsSuccessStatusCode) {
                // Handle 429 Explicitly
                if (response.StatusCode == HttpStatusCode.TooManyRequests) {
                    UpdateRateLimit(response.Headers.RetryAfter);
                    throw new HttpRequestException($"[{Name}] Rate Limited (429). Unavailable until {RateLimitedUntil.ToLocalTime():T}", null, HttpStatusCode.TooManyRequests);
                }

                var errorBody = await response.Content.ReadAsStringAsync(cts.Token);

                // Throwing here allows the LoadBalancer to catch it and try the next provider
                throw new HttpRequestException($"[{Name}] API Error {response.StatusCode}: {errorBody}", null, response.StatusCode);
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(jsonResponse);

            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0) {
                // Extract and Log Usage Stats
                int inputTokens = 0;
                int outputTokens = 0;

                if (doc.RootElement.TryGetProperty("usage", out var usage)) {
                    if (usage.TryGetProperty("prompt_tokens", out var pt)) inputTokens = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct)) outputTokens = ct.GetInt32();
                }

                Logger.LogDebug($"[{Name}] Completed request using {Model} | I: {inputTokens} | O: {outputTokens}");
                return choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            }

            return "";
        } catch (TaskCanceledException) {
            throw new TimeoutException($"[{Name}] Request timed out after {Timeout.TotalSeconds}s");
        }
    }

    private void UpdateRateLimit(RetryConditionHeaderValue? retryAfter) {
        TimeSpan delay = TimeSpan.FromSeconds(5); // Default backoff

        if (retryAfter != null) {
            if (retryAfter.Delta.HasValue) {
                delay = retryAfter.Delta.Value;
            } else if (retryAfter.Date.HasValue) {
                delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            }
        }

        // Add a small buffer (1s) to be safe
        if (delay < TimeSpan.Zero) delay = TimeSpan.FromSeconds(1);
        RateLimitedUntil = DateTimeOffset.UtcNow.Add(delay).AddSeconds(1);
    }
}