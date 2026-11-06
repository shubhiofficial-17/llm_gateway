using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlmGateway.Config;
using LlmGateway.Usage;

namespace LlmGateway.Providers;

/// <summary>
/// In-process fake LLM for demos, tests and load tests without API keys or cost.
/// Replies "[mock] You said: &lt;last user message&gt;" and reports usage like OpenAI does.
/// Put "[mock-error:503]" in the prompt to get that HTTP status back (used for fallback demos).
/// </summary>
public sealed partial class MockProvider(string name, ProviderOptions options) : IChatProvider
{
    public string Name => name;

    public Task<ProviderResponse> SendChatAsync(ProviderRequest request, CancellationToken ct)
    {
        var prompt = LastUserMessage(request.Body);

        var error = MockErrorPattern().Match(prompt);
        if (error.Success)
        {
            var status = int.Parse(error.Groups[1].Value);
            var errorJson = new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = $"Mock provider returned {status} on request.",
                    ["type"] = "mock_error",
                    ["code"] = $"mock_{status}",
                },
            };
            return Task.FromResult(JsonResponse(status, errorJson));
        }

        var reply = $"[mock] You said: {prompt}";
        var promptTokens = TokenEstimator.EstimateMessages(request.Body);
        var completionTokens = TokenEstimator.Estimate(reply);
        var id = "chatcmpl-mock-" + Guid.NewGuid().ToString("N")[..12];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!request.Stream)
        {
            var completion = new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion",
                ["created"] = created,
                ["model"] = request.UpstreamModel,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = reply },
                        ["finish_reason"] = "stop",
                    },
                },
                ["usage"] = Usage(promptTokens, completionTokens),
            };
            return Task.FromResult(JsonResponse(200, completion));
        }

        var includeUsage = request.Body["stream_options"] is JsonObject so
                           && so["include_usage"] is JsonValue v
                           && v.TryGetValue<bool>(out var b) && b;

        var pipe = new Pipe();
        _ = WriteStreamAsync(pipe.Writer, id, created, request.UpstreamModel, reply, includeUsage,
            promptTokens, completionTokens, ct);

        return Task.FromResult(new ProviderResponse
        {
            StatusCode = 200,
            ContentType = "text/event-stream",
            Body = pipe.Reader.AsStream(),
        });
    }

    private async Task WriteStreamAsync(PipeWriter writer, string id, long created, string model, string reply,
        bool includeUsage, int promptTokens, int completionTokens, CancellationToken ct)
    {
        try
        {
            var words = reply.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                var delta = new JsonObject { ["content"] = i == 0 ? words[i] : " " + words[i] };
                if (i == 0)
                {
                    delta["role"] = "assistant";
                }

                await WriteEventAsync(writer, Chunk(id, created, model, delta, finishReason: null), ct);
                if (options.MockTokenDelayMs > 0)
                {
                    await Task.Delay(options.MockTokenDelayMs, ct);
                }
            }

            await WriteEventAsync(writer, Chunk(id, created, model, new JsonObject(), finishReason: "stop"), ct);

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
                await WriteEventAsync(writer, usageChunk.ToJsonString(), ct);
            }

            await WriteEventAsync(writer, "[DONE]", ct);
            await writer.CompleteAsync();
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex is OperationCanceledException ? null : ex);
        }
    }

    private static async Task WriteEventAsync(PipeWriter writer, string data, CancellationToken ct)
    {
        await writer.WriteAsync(Encoding.UTF8.GetBytes($"data: {data}\n\n"), ct);
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

    private static ProviderResponse JsonResponse(int status, JsonObject body) => new()
    {
        StatusCode = status,
        ContentType = "application/json",
        Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString())),
    };

    private static string LastUserMessage(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages)
        {
            return "";
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is JsonObject m && m["role"] is JsonValue r && r.TryGetValue<string>(out var role) && role == "user")
            {
                return TokenEstimator.ContentText(m["content"]);
            }
        }

        return "";
    }

    [GeneratedRegex(@"\[mock-error:(\d{3})\]")]
    private static partial Regex MockErrorPattern();
}
