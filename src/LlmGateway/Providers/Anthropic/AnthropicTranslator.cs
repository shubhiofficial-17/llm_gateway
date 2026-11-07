using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmGateway.Usage;

namespace LlmGateway.Providers.Anthropic;

/// <summary>
/// Converts between OpenAI's chat completions format and Anthropic's Messages API, so clients keep
/// using OpenAI SDKs while the gateway calls Claude. Text content only; tools and images are not
/// translated yet. See ADR 0008.
/// </summary>
public static class AnthropicTranslator
{
    /// <summary>OpenAI request body → Anthropic /v1/messages body.</summary>
    public static JsonObject ToAnthropicRequest(JsonObject openAi, string upstreamModel, int defaultMaxTokens)
    {
        var system = new List<string>();
        var messages = new JsonArray();

        if (openAi["messages"] is JsonArray input)
        {
            foreach (var node in input)
            {
                if (node is not JsonObject message)
                {
                    continue;
                }

                var role = message["role"] is JsonValue r && r.TryGetValue<string>(out var s) ? s : "user";
                var text = TokenEstimator.ContentText(message["content"]);

                switch (role)
                {
                    // Anthropic takes system prompts as a top-level field, not as messages.
                    case "system" or "developer":
                        system.Add(text);
                        break;
                    case "assistant":
                        Append(messages, "assistant", text);
                        break;
                    default:
                        Append(messages, "user", text);
                        break;
                }
            }
        }

        var request = new JsonObject
        {
            ["model"] = upstreamModel,
            // Required by Anthropic; optional in OpenAI.
            ["max_tokens"] = ReadInt(openAi["max_completion_tokens"]) ?? ReadInt(openAi["max_tokens"]) ?? defaultMaxTokens,
            ["messages"] = messages,
        };

        if (system.Count > 0)
        {
            request["system"] = string.Join("\n\n", system);
        }

        CopyIfPresent(openAi, request, "temperature");
        CopyIfPresent(openAi, request, "top_p");

        switch (openAi["stop"])
        {
            case JsonValue single when single.TryGetValue<string>(out var stop):
                request["stop_sequences"] = new JsonArray(stop);
                break;
            case JsonArray many:
                request["stop_sequences"] = many.DeepClone();
                break;
        }

        if (openAi["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var stream) && stream)
        {
            request["stream"] = true;
        }

        return request;
    }

    /// <summary>Anthropic message → OpenAI chat.completion.</summary>
    public static JsonObject ToOpenAiCompletion(JsonObject anthropic, string model)
    {
        var text = new StringBuilder();
        if (anthropic["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block?["type"]?.GetValue<string>() == "text")
                {
                    text.Append(block["text"]?.GetValue<string>());
                }
            }
        }

        var promptTokens = ReadInt(anthropic["usage"]?["input_tokens"]) ?? 0;
        var completionTokens = ReadInt(anthropic["usage"]?["output_tokens"]) ?? 0;

        return new JsonObject
        {
            ["id"] = anthropic["id"]?.GetValue<string>() ?? "",
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text.ToString() },
                    ["finish_reason"] = FinishReason(anthropic["stop_reason"]?.GetValue<string>()),
                },
            },
            ["usage"] = Usage(promptTokens, completionTokens),
        };
    }

    /// <summary>Anthropic error body → OpenAI error body. Falls back to a generic message for non-JSON bodies.</summary>
    public static JsonObject ToOpenAiError(string body, int statusCode)
    {
        string? type = null;
        string? message = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject error)
            {
                type = error["type"]?.GetValue<string>();
                message = error["message"]?.GetValue<string>();
            }
        }
        catch (JsonException)
        {
        }

        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message ?? $"Anthropic returned HTTP {statusCode}.",
                ["type"] = type ?? "api_error",
                ["code"] = type switch
                {
                    "rate_limit_error" => "rate_limit_exceeded",
                    "overloaded_error" => "overloaded",
                    _ => type ?? "upstream_error",
                },
            },
        };
    }

    /// <summary>
    /// Anthropic uses 529 for "overloaded", which isn't a standard status. Report it as 503 so
    /// clients and retry policies treat it as a temporary server-side failure.
    /// </summary>
    public static int ToOpenAiStatus(int anthropicStatus) => anthropicStatus == 529 ? 503 : anthropicStatus;

    /// <summary>
    /// Rewrites Anthropic's event stream (message_start, content_block_delta, message_delta, message_stop)
    /// as OpenAI chat.completion.chunk events, ending with a usage chunk (if asked for) and [DONE].
    /// </summary>
    public static async Task TranslateStreamAsync(
        Stream anthropicSse, PipeWriter output, string model, bool includeUsage, CancellationToken ct)
    {
        using var reader = new StreamReader(anthropicSse, Encoding.UTF8);
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var promptTokens = 0;
        var completionTokens = 0;
        var finished = false;

        while (!finished && await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue; // "event:" lines repeat the type that's also in the data; blank lines end events
            }

            JsonObject? evt;
            try
            {
                evt = JsonNode.Parse(line.AsSpan(5).Trim().ToString()) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            switch (evt?["type"]?.GetValue<string>())
            {
                case "message_start":
                    id = evt["message"]?["id"]?.GetValue<string>() ?? id;
                    promptTokens = ReadInt(evt["message"]?["usage"]?["input_tokens"]) ?? 0;
                    completionTokens = ReadInt(evt["message"]?["usage"]?["output_tokens"]) ?? 0;
                    await WriteAsync(output, Chunk(id, created, model, new JsonObject { ["role"] = "assistant", ["content"] = "" }, null), ct);
                    break;

                case "content_block_delta" when IsTextDelta(evt):
                    var text = evt["delta"]?["text"]?.GetValue<string>() ?? "";
                    await WriteAsync(output, Chunk(id, created, model, new JsonObject { ["content"] = text }, null), ct);
                    break;

                case "message_delta":
                    // output_tokens here is the running total for the whole message.
                    completionTokens = ReadInt(evt["usage"]?["output_tokens"]) ?? completionTokens;
                    var stopReason = evt["delta"]?["stop_reason"]?.GetValue<string>();
                    if (stopReason is not null)
                    {
                        await WriteAsync(output, Chunk(id, created, model, new JsonObject(), FinishReason(stopReason)), ct);
                    }

                    break;

                case "message_stop":
                    finished = true;
                    break;

                case "error":
                    // Mid-stream failure (e.g. overloaded). Pass it on in OpenAI's error shape and stop.
                    await WriteAsync(output, ToOpenAiError(evt.ToJsonString(), 500).ToJsonString(), ct);
                    finished = true;
                    break;
            }
        }

        if (includeUsage)
        {
            var usageChunk = new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion.chunk",
                ["created"] = created,
                ["model"] = model,
                ["choices"] = new JsonArray(),
                ["usage"] = Usage(promptTokens, completionTokens),
            };
            await WriteAsync(output, usageChunk.ToJsonString(), ct);
        }

        await WriteAsync(output, "[DONE]", ct);
    }

    private static bool IsTextDelta(JsonObject evt) =>
        evt["delta"] is JsonObject delta && delta["type"]?.GetValue<string>() == "text_delta";

    internal static string? FinishReason(string? stopReason) => stopReason switch
    {
        null => null,
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" => "length",
        "tool_use" => "tool_calls",
        "refusal" => "content_filter",
        _ => "stop",
    };

    private static void Append(JsonArray messages, string role, string text)
    {
        // Anthropic requires user and assistant turns to alternate, so merge consecutive same-role messages.
        if (messages.Count > 0 && messages[^1] is JsonObject last && last["role"]?.GetValue<string>() == role)
        {
            last["content"] = last["content"]!.GetValue<string>() + "\n\n" + text;
            return;
        }

        messages.Add(new JsonObject { ["role"] = role, ["content"] = text });
    }

    private static void CopyIfPresent(JsonObject from, JsonObject to, string name)
    {
        if (from[name] is JsonValue value)
        {
            to[name] = value.DeepClone();
        }
    }

    private static string Chunk(string id, long created, string model, JsonObject delta, string? finishReason) =>
        new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason },
            },
        }.ToJsonString();

    private static JsonObject Usage(int promptTokens, int completionTokens) => new()
    {
        ["prompt_tokens"] = promptTokens,
        ["completion_tokens"] = completionTokens,
        ["total_tokens"] = promptTokens + completionTokens,
    };

    private static async Task WriteAsync(PipeWriter output, string data, CancellationToken ct)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes($"data: {data}\n\n"), ct);
    }

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var i) ? i : null;
}
