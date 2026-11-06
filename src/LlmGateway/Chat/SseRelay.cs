using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmGateway.Chat;

public sealed record SseRelayResult(int? PromptTokens, int? CompletionTokens, int CompletionChars, bool SawDone);

/// <summary>
/// Relays a Server-Sent Events stream event by event, flushing each one immediately so the client
/// sees tokens as they are generated. While relaying it reads token usage from the stream.
///
/// The gateway always asks upstream for a final usage chunk (stream_options.include_usage) so it
/// can bill accurately. If the client did not ask for that chunk, it is dropped here so the client
/// gets exactly the stream it requested.
/// </summary>
public static class SseRelay
{
    public static async Task<SseRelayResult> RelayAsync(
        Stream upstream, Stream downstream, bool forwardUsageOnlyChunks, CancellationToken ct)
    {
        var state = new RelayState();
        using var reader = new StreamReader(upstream, Encoding.UTF8);

        var eventLines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                await ProcessEventAsync(eventLines, state, downstream, forwardUsageOnlyChunks, ct);
                continue;
            }

            eventLines.Add(line);
        }

        // Upstream closed without a trailing blank line: still deliver the last event.
        await ProcessEventAsync(eventLines, state, downstream, forwardUsageOnlyChunks, ct);

        return new SseRelayResult(state.PromptTokens, state.CompletionTokens, state.CompletionChars, state.SawDone);
    }

    private static async Task ProcessEventAsync(
        List<string> eventLines, RelayState state, Stream downstream, bool forwardUsageOnlyChunks, CancellationToken ct)
    {
        if (eventLines.Count == 0)
        {
            return;
        }

        var forward = true;
        var data = DataPayload(eventLines);

        if (data == "[DONE]")
        {
            state.SawDone = true;
        }
        else if (data is not null && TryParseObject(data) is { } chunk)
        {
            if (chunk["usage"] is JsonObject usage)
            {
                state.PromptTokens = ReadInt(usage["prompt_tokens"]) ?? state.PromptTokens;
                state.CompletionTokens = ReadInt(usage["completion_tokens"]) ?? state.CompletionTokens;
            }

            if (chunk["choices"] is JsonArray choices)
            {
                foreach (var choice in choices)
                {
                    if (choice?["delta"]?["content"] is JsonValue content && content.TryGetValue<string>(out var text))
                    {
                        state.CompletionChars += text.Length;
                    }
                }

                var isUsageOnly = choices.Count == 0 && chunk["usage"] is JsonObject;
                forward = forwardUsageOnlyChunks || !isUsageOnly;
            }
        }

        if (forward)
        {
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', eventLines) + "\n\n");
            await downstream.WriteAsync(bytes, ct);
            await downstream.FlushAsync(ct);
        }

        eventLines.Clear();
    }

    /// <summary>Joins the "data:" lines of one event, per the SSE spec. Returns null if there are none.</summary>
    private static string? DataPayload(List<string> eventLines)
    {
        StringBuilder? payload = null;
        foreach (var line in eventLines)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line.AsSpan(5);
            if (value.Length > 0 && value[0] == ' ')
            {
                value = value[1..];
            }

            if (payload is null)
            {
                payload = new StringBuilder();
            }
            else
            {
                payload.Append('\n');
            }

            payload.Append(value);
        }

        return payload?.ToString();
    }

    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var i) ? i : null;

    private sealed class RelayState
    {
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int CompletionChars { get; set; }
        public bool SawDone { get; set; }
    }
}
