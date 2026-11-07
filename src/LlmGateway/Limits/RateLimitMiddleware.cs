using System.Globalization;
using LlmGateway.Auth;
using Microsoft.Extensions.Options;

namespace LlmGateway.Limits;

/// <summary>
/// Per-team requests-per-minute limit on chat completions. Runs after API key auth.
/// If Redis is down the request is let through (fail open): a short gap in rate limiting
/// is better than taking every AI feature offline. See ADR 0005.
/// </summary>
public sealed class RateLimitMiddleware(RequestDelegate next, ILogger<RateLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext ctx, IRateLimiter limiter, IOptionsMonitor<RateLimitOptions> options)
    {
        if (!IsChatCompletion(ctx.Request))
        {
            await next(ctx);
            return;
        }

        var team = ctx.GetTeam();
        var rpm = team.RequestsPerMinute ?? options.CurrentValue.RequestsPerMinute;
        if (rpm <= 0)
        {
            await next(ctx);
            return;
        }

        RateLimitDecision decision;
        try
        {
            decision = await limiter.TryAcquireAsync($"rl:{{{team.Id}}}:rpm", rpm, rpm / 60d, cost: 1);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Rate limiter unavailable; allowing request for team {TeamId}", team.Id);
            await next(ctx);
            return;
        }

        ctx.Response.Headers["x-ratelimit-limit-requests"] = rpm.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["x-ratelimit-remaining-requests"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (!decision.Allowed)
        {
            var retrySeconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
            ctx.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
            await OpenAiError.WriteAsync(ctx, StatusCodes.Status429TooManyRequests,
                $"Rate limit reached for team '{team.Id}': {rpm} requests per minute. Try again in {retrySeconds}s.",
                "requests", "rate_limit_exceeded");
            return;
        }

        await next(ctx);
    }

    private static bool IsChatCompletion(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && request.Path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase);
}
