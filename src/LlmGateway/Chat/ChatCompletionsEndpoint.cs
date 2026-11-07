using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmGateway.Auth;
using LlmGateway.Limits;
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
        TokenRateLimit tokenLimit,
        BudgetGuard budget,
        FallbackSender sender,
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

        // Worst case for this request, held against the team's limits until the real usage is known.
        var promptEstimate = TokenEstimator.EstimateMessages(body);
        var maxCompletion = tokenLimit.MaxCompletionTokens(body);

        var reservation = await tokenLimit.TryReserveAsync(ctx, team, promptEstimate + maxCompletion);
        if (reservation is null)
        {
            return; // over the team's token limit; the error has been written
        }

        // Reserve for the priciest model the request could end up on.
        var worstCaseCost = route.Chain.Max(r => Pricing.CostMicros(r.Options, promptEstimate, maxCompletion));
        var budgetReservation = await budget.TryReserveAsync(ctx, team, worstCaseCost);
        if (budgetReservation is null)
        {
            await tokenLimit.SettleAsync(reservation, actualTokens: 0);
            return; // over budget, or the budget couldn't be checked; the error has been written
        }

        var requestId = "req_" + Guid.NewGuid().ToString("N");
        ctx.Response.Headers["x-request-id"] = requestId;

        var started = Stopwatch.GetTimestamp();
        var served = route;
        var status = 0;
        int? promptTokens = null;
        int? completionTokens = null;
        var completionChars = 0;

        try
        {
            var sent = await sender.SendAsync(route, upstreamBody, stream, requestId, ct);
            served = sent.Served;
            ctx.Response.Headers["x-gateway-model"] = served.Model;
            ctx.Response.Headers["x-gateway-provider"] = served.Provider.Name;
            if (sent.Attempts > 1)
            {
                ctx.Response.Headers["x-gateway-fallback-from"] = route.Model;
            }

            if (sent.Response is null)
            {
                status = sent.FailureStatus;
                await OpenAiError.WriteAsync(ctx, status, sent.FailureMessage!, "gateway_error", sent.FailureCode!);
                return;
            }

            var upstream = sent.Response;
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
                            Pricing.CostMicros(served.Options, p, c).ToString(System.Globalization.CultureInfo.InvariantCulture);
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
            log.LogWarning(ex, "Stream from {Provider} broke for {RequestId}", served.Provider.Name, requestId);
        }
        finally
        {
            // Bill successful requests, and requests the client abandoned (the provider still charged for them).
            var billable = status is >= 200 and < 300 or ClientClosedRequest;
            var usageEstimated = billable && (promptTokens is null || completionTokens is null);
            var finalPrompt = billable ? promptTokens ?? promptEstimate : 0;
            var finalCompletion = billable ? completionTokens ?? TokenEstimator.FromChars(completionChars) : 0;

            // Billed at the price of the model that actually answered.
            var cost = Pricing.CostMicros(served.Options, finalPrompt, finalCompletion);

            await tokenLimit.SettleAsync(reservation, finalPrompt + finalCompletion);
            await budget.SettleAsync(budgetReservation, cost);

            usageRecorder.Record(new UsageEvent(
                requestId,
                team.Id,
                served.Model,
                served.Provider.Name,
                finalPrompt,
                finalCompletion,
                cost,
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
