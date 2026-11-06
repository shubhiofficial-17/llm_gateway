using System.Text.Json.Nodes;

namespace LlmGateway.Usage;

/// <summary>
/// Rough token estimate (~4 characters per token for English text). Used only when a provider
/// does not report usage, e.g. a client disconnects mid-stream. Such rows are flagged usage_estimated.
/// </summary>
public static class TokenEstimator
{
    private const int CharsPerToken = 4;
    private const int PerMessageOverhead = 4;

    public static int FromChars(int chars) => chars <= 0 ? 0 : (chars + CharsPerToken - 1) / CharsPerToken;

    public static int Estimate(string? text) => FromChars(text?.Length ?? 0);

    public static int EstimateMessages(JsonObject chatRequest)
    {
        if (chatRequest["messages"] is not JsonArray messages)
        {
            return 0;
        }

        var tokens = 0;
        foreach (var message in messages)
        {
            tokens += PerMessageOverhead + Estimate(ContentText(message?["content"]));
        }

        return tokens;
    }

    /// <summary>Message content is either a string or an array of parts like {"type":"text","text":"..."}.</summary>
    public static string ContentText(JsonNode? content)
    {
        switch (content)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return text;
            case JsonArray parts:
                var texts = parts
                    .Select(p => p is JsonObject o && o["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null)
                    .Where(s => s is not null);
                return string.Join("\n", texts);
            default:
                return "";
        }
    }
}
