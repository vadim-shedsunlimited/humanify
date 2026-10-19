using TiktokenSharp;

internal static class TokenHelper {
    private static TikToken? _encoding;

    // Unified ratio: 3 characters per token is a safe conservative estimate for code
    public const int CharsPerToken = 3;

    // GPT-4 and GPT-3.5-turbo use 'cl100k_base' encoding.
    // We initialize it lazily to avoid startup overhead if not used.
    private static TikToken Encoding => _encoding ??= TikToken.GetEncoding("cl100k_base");

    public static int EstimateTokenCount(string? text) {
        if (string.IsNullOrEmpty(text)) return 0;
        try {
            // Encode the text and count the tokens
            return Encoding.Encode(text).Count;
        } catch {
            // Conservative estimate: 1 token ~= 3 chars for code (due to many symbols)
            return text.Length / 3;
        }
    }

    /// <summary>
    /// Precisely truncates a string to a token limit using the actual encoder.
    /// </summary>
    public static string TruncateToTokenLimit(string text, int tokenLimit) {
        try {
            var tokens = Encoding.Encode(text);
            if (tokens.Count <= tokenLimit) return text;

            // Keep the first half and last half of the allowed tokens
            int half = tokenLimit / 2;
            var startTokens = tokens.GetRange(0, half);
            var endTokens = tokens.GetRange(tokens.Count - half, half);

            return Encoding.Decode(startTokens) +
                   "\n// ... [Token Limit Exceeded: Content Truncated] ... \n" +
                   Encoding.Decode(endTokens);
        } catch {
            // Fallback to character-based truncation if encoding fails
            int charLimit = tokenLimit * CharsPerToken;
            if (text.Length <= charLimit) return text;
            return text[..(charLimit / 2)] + "\n// ... [Truncated] ... \n" + text[^(charLimit / 2)..];
        }
    }
}