using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmGateway.Auth;
using LlmGateway.Caching;
using LlmGateway.Limits;
using LlmGateway.Providers;
using LlmGateway.Telemetry;
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
        ResponseCaching caching,
        GatewayTelemetry telemetry,
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

        // Response cache (opt-in, non-streaming only). A hit costs nothing and skips the limits below.
        CacheLookup? cacheLookup = null;
        if (caching.IsRequested(ctx.Request, stream))
        {
            cacheLookup = await caching.LookupAsync(team, body, ct);
            if (cacheLookup.Hit is not null)
            {
                await ServeCacheHitAsync(ctx, cacheLookup, team, usageRecorder, time);
                return;
            }

            ctx.Response.Headers["x-gateway-cache"] = "miss";
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
            if (!string.Equals(served.Model, route.Model, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers["x-gateway-fallback-from"] = route.Model;
                if (sent.Response is not null)
                {
                    telemetry.FallbackUsed(route.Model, served.Model);
                }
            }

            if (sent.Response is null)
            {
                status = sent.FailureStatus;
                if (sent.RetryAfter is { } retryAfter)
                {
                    ctx.Response.Headers.RetryAfter =
                        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

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

                    // Only answers from the requested model are cached, never a fallback's.
                    if (cacheLookup is not null && served == route && promptTokens is { } cp && completionTokens is { } cc)
                    {
                        await caching.StoreAsync(cacheLookup, new CachedCompletion(
                            Encoding.UTF8.GetString(buffer.ToArray()), served.Model, cp, cc, Pricing.CostMicros(served.Options, cp, cc)));
                    }

                    return;
                }

                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

                var servedModel = served;
                var result = await SseRelay.RelayAsync(upstream.Body, ctx.Response.Body, clientWantsUsageChunk, ct,
                    onFirstEvent: () => telemetry.FirstToken(servedModel.Model, servedModel.Provider.Name, Stopwatch.GetElapsedTime(started)));
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

            // Enrich the HTTP server span so a trace shows who called what and what it cost.
            Activity.Current?.SetTag("gateway.team", team.Id);
            Activity.Current?.SetTag("gen_ai.request.model", route.Model);
            Activity.Current?.SetTag("gen_ai.response.model", served.Model);
            Activity.Current?.SetTag("gen_ai.usage.input_tokens", finalPrompt);
            Activity.Current?.SetTag("gen_ai.usage.output_tokens", finalCompletion);
            Activity.Current?.SetTag("gateway.cost_micros", cost);
            Activity.Current?.SetTag("gateway.cache", cacheLookup?.Kind ?? "bypass");

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

    private static async Task ServeCacheHitAsync(
        HttpContext ctx, CacheLookup lookup, Team team, IUsageRecorder usageRecorder, TimeProvider time)
    {
        var hit = lookup.Hit!;
        var requestId = "req_" + Guid.NewGuid().ToString("N");
        ctx.Response.Headers["x-request-id"] = requestId;
        ctx.Response.Headers["x-gateway-cache"] = lookup.Kind;
        if (lookup.Similarity is { } similarity)
        {
            ctx.Response.Headers["x-gateway-cache-similarity"] = similarity.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        }

        ctx.Response.Headers["x-gateway-model"] = hit.Model;
        ctx.Response.Headers["x-gateway-cost-micros"] = "0";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(hit.Body, ctx.RequestAborted);

        // Recorded with the original token counts so reports show what the hit saved.
        var provider = lookup.Similarity is null ? "cache" : "semantic-cache";
        usageRecorder.Record(new UsageEvent(requestId, team.Id, hit.Model, provider, hit.PromptTokens, hit.CompletionTokens,
            CostMicros: 0, StatusCode: 200, LatencyMs: 0, Streamed: false, UsageEstimated: false, time.GetUtcNow(),
            CacheHit: true, SavedMicros: hit.CostMicros));
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
