using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acornima.Ast;

namespace Humanify.Plugins.OpenAI;

using AstProgram = Acornima.Ast.Program;

public abstract partial class OpenAIBasePlugin : IPlugin {
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string Identifier { get; }
    public string InstanceId { get; } = Guid.NewGuid().ToString();

    public virtual double Weight { get; } = 1.0;

    // Pool of Providers
    protected readonly List<OpenAIClient> _clients;
    protected readonly int _contextWindowSize;

    // Load Balancing State
    private int _currentClientIndex = 0;
    private readonly object _lock = new();


    // Path resolution
    protected readonly string _inputDir;
    protected readonly string _resultDir;

    // Constants shared by plugins

    // Use the unified ratio for the initial file read limit
    protected const int MAX_IMPORTED_FILE_TOKENS = 600;
    protected static readonly int MAX_IMPORTED_FILE_CHARS = MAX_IMPORTED_FILE_TOKENS * TokenHelper.CharsPerToken;

    // Reserve space for instructions and the expected AI response
    private const int SYSTEM_RESERVE_TOKENS = 500;

    private const double MAX_SCOPE_PERCENTAGE = 0.6;

    /// <summary>
    /// Context Buffer (e.g., 2000 chars ~ 50 lines) to show surrounding logic
    /// </summary>
    protected const int CONTEXT_CHARS_BUFFER = 2000;
    // Global max attempts = (Retries per client * clients) or just a fixed number
    private const int MAX_TOTAL_ATTEMPTS = 10;
    private const int BASE_RETRY_DELAY_MS = 2000;


    private static readonly HttpClient _httpClient = new() {
        Timeout = TimeSpan.FromMinutes(10)
    };

    protected OpenAIBasePlugin(
        List<OpenAIClient> clients,
        int contextWindowSize,
        string baseOutputDir
    ) {
        if (clients == null || clients.Count == 0) {
            throw new ArgumentException("At least one AI provider must be configured.");
        }
        _clients = clients;
        _contextWindowSize = contextWindowSize;
        _inputDir = Path.Combine(baseOutputDir, "input");
        _resultDir = Path.Combine(baseOutputDir, "result");
    }

    public virtual void InvalidateCache(string filePath, ConcurrentDictionary<string, string> pluginCache) {
        // Default implementation does nothing
    }


    public abstract Task<(string Code, AstProgram? Ast)> ProcessAsync(PluginContext context);

    protected async Task<string> LoadFileContent(PluginContext context, string currentFileRelativePath, string importRelativePath) {
        // Resolve path: currentFile is "folder/a.js", import is "./b.js" -> "folder/b.js"
        var currentDir = Path.GetDirectoryName(currentFileRelativePath) ?? "";

        // Normalize path to handle ./ and ../
        // We resolve it against the input directory to get a canonical structure
        // purely for the sake of the Dictionary Key.
        var rawCombined = Path.Combine(currentDir, importRelativePath);
        var absInputDir = Path.GetFullPath(_inputDir);
        var absTarget = Path.GetFullPath(Path.Combine(absInputDir, rawCombined));

        // Generate a canonical relative path to use as the Cache Key
        // This ensures that "utils.js" and "./utils.js" map to the same key
        var canonicalKey = Path.GetRelativePath(absInputDir, absTarget).Replace("\\", "/");

        // Try Cache first (Shared across all plugins)
        if (context.SharedFileCache.TryGetValue(canonicalKey, out var cached)) return cached;

        // Try Result Dir (Priority: It might be already unminified!)
        // Note: We use canonicalKey here because it is clean (e.g. "folder/b.js")
        var resultPath = Path.GetFullPath(Path.Combine(_resultDir, canonicalKey));
        if (File.Exists(resultPath)) {
            Logger.LogDebug($"[Context] Loading import from result: {canonicalKey}");
            return await ReadAndCache(context, canonicalKey, resultPath);
        }

        // Try Input Dir (Fallback)
        // We use absTarget here since we already calculated the full input path
        if (File.Exists(absTarget)) {
            Logger.LogDebug($"[Context] Loading import from input: {canonicalKey}");
            return await ReadAndCache(context, canonicalKey, absTarget);
        }

        Logger.LogWarning($"[Context] Import not found: {canonicalKey}");
        return "";
    }

    private static async Task<string> ReadAndCache(PluginContext context, string key, string fsPath) {
        try {
            var text = await File.ReadAllTextAsync(fsPath);
            // Truncate to save tokens
            if (text.Length > MAX_IMPORTED_FILE_CHARS) {
                text = text.Substring(0, MAX_IMPORTED_FILE_CHARS) + "\n// ... (file truncated)";
            }
            context.SharedFileCache[key] = text;
            return text;
        } catch {
            return "";
        }
    }

    /// <summary>
    /// Returns a slice of the scope that focuses on the specific usage nodes provided.
    /// Used for huge functions where we only want to show the LLM the relevant lines (plus context).
    /// </summary>
    protected string GetContextualScope(string code, Node scopeNode, IEnumerable<Node> focusNodes, int? systemPromptTokenEstimate = null) {
        var nodes = focusNodes.OrderBy(n => n.Range.Start).ToList();

        // If no focus nodes, fall back to standard behavior
        if (nodes.Count == 0) return GetCodeForScope(code, scopeNode, systemPromptTokenEstimate);

        // Calculate limits first
        int reserve = systemPromptTokenEstimate ?? SYSTEM_RESERVE_TOKENS;
        int availableWindow = _contextWindowSize - reserve;
        int tokenLimit = (int)(availableWindow * MAX_SCOPE_PERCENTAGE);
        // Conservative char limit (3 chars per token) to perform fast bounding box checks
        int charLimit = tokenLimit * 3;

        // Calculate Bounding Box of ALL relevant nodes
        int minStart = nodes[0].Range.Start;
        int maxEnd = nodes[^1].Range.End;

        // Smart Windowing: If the spread is too large, shrink to the first cluster of usages
        if ((maxEnd - minStart) + (2 * CONTEXT_CHARS_BUFFER) > charLimit) {
            // Logger.LogDebug($"[Token Management] Bounding box too large ({(maxEnd - minStart)} chars). Shrinking to fit primary usages.");

            // Find the last node that fits within the limit relative to the start
            int validEnd = minStart;
            foreach (var node in nodes) {
                // If adding this node pushes us over the limit (considering buffer), stop.
                if ((node.Range.End - minStart) + (2 * CONTEXT_CHARS_BUFFER) > charLimit) {
                    break;
                }
                validEnd = node.Range.End;
            }
            maxEnd = validEnd;
        }

        // Clamp to Scope Boundaries (don't go outside the function)
        int sliceStart = Math.Max(scopeNode.Range.Start, minStart - CONTEXT_CHARS_BUFFER);
        int sliceEnd = Math.Min(scopeNode.Range.End, maxEnd + CONTEXT_CHARS_BUFFER);

        // Construct the slice
        var sb = new StringBuilder();

        if (sliceStart > scopeNode.Range.Start + 100) {
            sb.AppendLine("// ... (code preceding this batch skipped) ...");
        }

        string slice = code.Substring(sliceStart, sliceEnd - sliceStart);
        sb.Append(slice);

        if (sliceEnd < scopeNode.Range.End - 100) {
            sb.AppendLine("\n// ... (code following this batch skipped) ...");
        }

        string contextCode = sb.ToString();

        // Final Safety Check
        // Even with the Smart Window, complex code might be token-heavy.
        int estimatedTokens = TokenHelper.EstimateTokenCount(contextCode);

        if (estimatedTokens > tokenLimit) {
            Logger.LogWarning($"[Token Management] Contextual Scope still exceeds limit ({estimatedTokens}/{tokenLimit}). Truncating...");
            return TokenHelper.TruncateToTokenLimit(contextCode, tokenLimit);
        }

        return contextCode;
    }

    protected string GetCodeForScope(string code, Node scopeNode, int? systemPromptTokenEstimate = null) {
        // If it's a huge program, trim it? For now, let's try to grab the exact range.
        // We add a bit of buffer if possible, or just the exact node range.
        int start = scopeNode.Range.Start;
        int end = scopeNode.Range.End;

        // Defensive checks for code slicing
        if (start < 0) start = 0;
        if (end > code.Length) end = code.Length;

        // If range is invalid, return whole code or empty
        if (end < start) return "";

        int length = end - start;
        string slice = code.Substring(start, length);

        // Use token estimate instead of raw char length for decision
        int estimatedTokens = TokenHelper.EstimateTokenCount(slice);

        // Calculate available window: (Total - System/Response Reserve) * Max Scope %
        int reserve = systemPromptTokenEstimate ?? SYSTEM_RESERVE_TOKENS;
        int availableWindow = _contextWindowSize - reserve;
        int tokenLimit = (int)(availableWindow * MAX_SCOPE_PERCENTAGE);

        if (estimatedTokens > tokenLimit) {
            Logger.LogWarning($"[Token Management] Scope exceeds limit ({estimatedTokens}/{tokenLimit} tokens). Truncating...");
            return TokenHelper.TruncateToTokenLimit(slice, tokenLimit);
        }
        return slice;
    }

    /// <summary>
    /// Executes a prompt using Smart Load Balancing, Failover, and Rate-Limit Awareness.
    /// </summary>
    protected async Task<string> CallApiWithRetries(string systemPrompt, string userPrompt, int maxTokens = 1000) {
        int attempts = 0;

        // Calculate the TOTAL size of this specific request
        int inputTokens = TokenHelper.EstimateTokenCount(systemPrompt + userPrompt);
        int requiredCapacity = inputTokens + maxTokens + 100; // +buffer

        List<string> errors = [];

        while (attempts < MAX_TOTAL_ATTEMPTS) {
            attempts++;

            // Get Next Available Client (Handling Global Rate Limits)
            OpenAIClient client = await GetNextAvailableClientAsync(requiredCapacity);

            try {
                // Wait for capacity on THIS specific provider
                await client.Semaphore.WaitAsync();

                try {
                    // Execute
                    return await client.CompleteAsync(systemPrompt, userPrompt, maxTokens);
                } finally {
                    client.Semaphore.Release();
                }
            } catch (Exception ex) {
                // Handle Failures
                // Note: If CompleteAsync threw a RateLimit exception, the client's internal state 
                // is already updated, so GetNextAvailableClientAsync will skip it next time.

                string errorMsg = ex.Message;
                errors.Add($"{client.Name}: {errorMsg}");

                // If Rate Limited (429), maybe wait a bit? 
                // Currently we just failover to the next provider immediately, which is faster.
                Logger.LogWarning($"[LoadBalancer] Provider '{client.Name}' failed ({ex.GetType().Name}). Failing over... Error: {errorMsg}");

                // If it wasn't a rate limit (e.g. 500 Error), add a small delay before failover
                if (!client.IsRateLimited) {
                    await Task.Delay(500);
                }
            }
        }

        throw new Exception($"All providers failed after {attempts} attempts. Errors: {string.Join("; ", errors)}");
    }

    /// <summary>
    /// Finds the next client that isn't rate limited. 
    /// If ALL are rate limited, waits until the earliest one becomes available.
    /// </summary>
    private async Task<OpenAIClient> GetNextAvailableClientAsync(int requiredTokens) {
        while (true) {
            OpenAIClient? selected = null;
            DateTimeOffset minWaitTime = DateTimeOffset.MaxValue;
            bool allRateLimited = true;

            // Track if we found ANY client that supports this size (even if rate limited)
            bool capacityExists = false;

            // Round-robin scan for an available client
            lock (_lock) {
                int start = (_currentClientIndex + 1) % _clients.Count;
                for (int i = 0; i < _clients.Count; i++) {
                    int idx = (start + i) % _clients.Count;
                    var c = _clients[idx];

                    if (c.ContextSize < requiredTokens) continue;

                    // Track that we found a provider capable of this size!
                    capacityExists = true;

                    if (!c.IsRateLimited) {
                        _currentClientIndex = idx;
                        selected = c;
                        allRateLimited = false;
                        break;
                    }

                    // Track the soonest availability in case everyone is blocked
                    if (c.RateLimitedUntil < minWaitTime) {
                        minWaitTime = c.RateLimitedUntil;
                    }
                }
            }

            if (selected != null) {
                return selected;
            }

            if (!capacityExists) {
                // Critical Error: We generated a batch so big NO provider can take it.
                throw new Exception($"[LoadBalancer] Request too large ({requiredTokens} tokens). No configured provider has sufficient context window.");
            }

            // If we are here, ALL clients are rate limited.
            if (allRateLimited && minWaitTime < DateTimeOffset.MaxValue) {
                var delay = minWaitTime - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) {
                    Logger.LogWarning($"[LoadBalancer] All providers rate-limited. Global pause for {delay.TotalSeconds:F1}s...");
                    await Task.Delay(delay);
                }
                // Loop back and try again
                continue;
            }

            // Fallback (should rarely happen unless list is empty)
            await Task.Delay(1000);
        }
    }

    protected Dictionary<string, string> ParseJsonResponse(string content) {
        Dictionary<string, string> result = [];
        try {
            // Remove Markdown code blocks if present
            content = MarkdownCodeJsonRegex().Replace(content, "");
            content = MarkdownCodeRegex().Replace(content, "");

            // Try direct JSON parse
            var doc = JsonDocument.Parse(content);
            foreach (var prop in doc.RootElement.EnumerateObject()) {
                var val = prop.Value.GetString();
                if (!string.IsNullOrEmpty(val)) result[prop.Name] = val;
            }
        } catch {
            // Fallback: Regex for simple "key": "value" lines if JSON fails
            var matches = FallbackRegex().Matches(content);
            foreach (Match m in matches) {
                result[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }
        return result;
    }

    private HttpRequestMessage CloneHttpRequestMessage(HttpRequestMessage req) {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri) {
            Content = req.Content // Be careful with streams, but StringContent is fine
        };
        foreach (var header in req.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }

    // FIX: Updated Regex to handle escaped quotes inside the value string
    [GeneratedRegex(@"""([^""]+)""\s*:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex FallbackRegex();

    [GeneratedRegex(@"^```json\s*", RegexOptions.Multiline)]
    private static partial Regex MarkdownCodeJsonRegex();

    [GeneratedRegex(@"^```\s*", RegexOptions.Multiline)]
    private static partial Regex MarkdownCodeRegex();
}