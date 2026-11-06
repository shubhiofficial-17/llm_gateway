using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmGateway.Auth;
using LlmGateway.Providers;
using LlmGateway.Usage;
using Microsoft.AspNetCore.Http.Features;

namespace LlmGateway.Chat;

/// <summary>POST /v1/chat/completions — OpenAI-compatible, streaming and non-streaming.</summary>
public static class ChatCompletionsEndpoint
{
    /// <summary>Not a real HTTP status: nginx's convention for "client closed the request".</summary>
    public const int ClientClosedRequest = 499;

    public static async Task HandleAsync(
        HttpContext ctx,
        ProviderRegistry registry,
        IUsageRecorder usageRecorder,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger(typeof(ChatCompletionsEndpoint));
        var ct = ctx.RequestAborted;
        var team = ctx.GetTeam();

        JsonObject? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is null)
        {
            await OpenAiError.WriteAsync(ctx, StatusCodes.Status400BadRequest,
                "Request body must be a JSON object.", "invalid_request_error", "invalid_json");
            return;
        }

        if (body["model"] is not JsonValue modelValue
            || !modelValue.TryGetValue<string>(out var model)
            || string.IsNullOrWhiteSpace(model))
        {
            await OpenAiError.WriteAsync(ctx, StatusCodes.Status400BadRequest,
                "'model' is required.", "invalid_request_error", "missing_model");
            return;
        }

        if (!registry.TryResolve(model, out var route))
        {
            await OpenAiError.WriteAsync(ctx, StatusCodes.Status404NotFound,
                $"The model '{model}' does not exist on this gateway.", "invalid_request_error", "model_not_found");
            return;
        }

        var stream = ReadBool(body["stream"]);
        var clientWantsUsageChunk = body["stream_options"] is JsonObject clientStreamOptions
                                    && ReadBool(clientStreamOptions["include_usage"]);

        var upstreamBody = (JsonObject)body.DeepClone();
        upstreamBody["model"] = route.UpstreamModel;
        if (stream)
        {
            // Always ask for the usage chunk so streamed requests are billed on real token counts.
            if (upstreamBody["stream_options"] is not JsonObject upstreamStreamOptions)
            {
                upstreamStreamOptions = new JsonObject();
                upstreamBody["stream_options"] = upstreamStreamOptions;
            }

            upstreamStreamOptions["include_usage"] = true;
        }

        var requestId = "req_" + Guid.NewGuid().ToString("N");
        ctx.Response.Headers["x-request-id"] = requestId;
        ctx.Response.Headers["x-gateway-provider"] = route.Provider.Name;

        var started = Stopwatch.GetTimestamp();
        var status = 0;
        int? promptTokens = null;
        int? completionTokens = null;
        var completionChars = 0;

        try
        {
            ProviderResponse upstream;
            try
            {
                upstream = await route.Provider.SendChatAsync(
                    new ProviderRequest(requestId, route.UpstreamModel, upstreamBody, stream), ct);
            }
            catch (HttpRequestException ex)
            {
                status = StatusCodes.Status502BadGateway;
                log.LogWarning(ex, "Provider {Provider} unreachable for {RequestId}", route.Provider.Name, requestId);
                await OpenAiError.WriteAsync(ctx, status,
                    $"Upstream provider '{route.Provider.Name}' is unreachable.", "gateway_error", "upstream_unreachable");
                return;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                status = StatusCodes.Status504GatewayTimeout;
                log.LogWarning("Provider {Provider} timed out for {RequestId}", route.Provider.Name, requestId);
                await OpenAiError.WriteAsync(ctx, status,
                    $"Upstream provider '{route.Provider.Name}' timed out.", "gateway_error", "upstream_timeout");
                return;
            }

            await using (upstream)
            {
                status = upstream.StatusCode;

                if (!upstream.IsSuccess)
                {
                    // Pass provider errors through unchanged (status and body) so clients see e.g. a real 429.
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = upstream.ContentType ?? "application/json";
                    await upstream.Body.CopyToAsync(ctx.Response.Body, ct);
                    return;
                }

                if (!stream)
                {
                    using var buffer = new MemoryStream();
                    await upstream.Body.CopyToAsync(buffer, ct);
                    (promptTokens, completionTokens) = ReadCompletionUsage(buffer.ToArray());

                    if (promptTokens is { } p && completionTokens is { } c)
                    {
                        ctx.Response.Headers["x-gateway-cost-micros"] =
                            Pricing.CostMicros(route.Options, p, c).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = upstream.ContentType ?? "application/json";
                    buffer.Position = 0;
                    await buffer.CopyToAsync(ctx.Response.Body, ct);
                    return;
                }

                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

                var result = await SseRelay.RelayAsync(upstream.Body, ctx.Response.Body, clientWantsUsageChunk, ct);
                promptTokens = result.PromptTokens;
                completionTokens = result.CompletionTokens;
                completionChars = result.CompletionChars;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = ClientClosedRequest;
            log.LogInformation("Client closed {RequestId} before completion", requestId);
        }
        catch (IOException ex) when (ctx.Response.HasStarted)
        {
            // Upstream broke mid-stream. Headers are already sent, so all we can do is end the response.
            log.LogWarning(ex, "Stream from {Provider} broke for {RequestId}", route.Provider.Name, requestId);
        }
        finally
        {
            // Bill successful requests, and requests the client abandoned (the provider still charged for them).
            var billable = status is >= 200 and < 300 or ClientClosedRequest;
            var usageEstimated = billable && (promptTokens is null || completionTokens is null);
            var finalPrompt = billable ? promptTokens ?? TokenEstimator.EstimateMessages(body) : 0;
            var finalCompletion = billable ? completionTokens ?? TokenEstimator.FromChars(completionChars) : 0;

            usageRecorder.Record(new UsageEvent(
                requestId,
                team.Id,
                route.Model,
                route.Provider.Name,
                finalPrompt,
                finalCompletion,
                Pricing.CostMicros(route.Options, finalPrompt, finalCompletion),
                status,
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                stream,
                usageEstimated,
                time.GetUtcNow()));
        }
    }

    private static bool ReadBool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var b) && b;

    private static (int? Prompt, int? Completion) ReadCompletionUsage(byte[] json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject root && root["usage"] is JsonObject usage)
            {
                return (SseRelay.ReadInt(usage["prompt_tokens"]), SseRelay.ReadInt(usage["completion_tokens"]));
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall back to estimates.
        }

        return (null, null);
    }
}
