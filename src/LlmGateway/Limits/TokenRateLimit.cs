using System.Globalization;
using System.Text.Json.Nodes;
using LlmGateway.Auth;
using Microsoft.Extensions.Options;

namespace LlmGateway.Limits;

/// <summary>Tokens taken from a team's bucket for one request, to be settled when the real count is known.</summary>
public sealed record TokenReservation(string Key, int Capacity, int Reserved)
{
    public static readonly TokenReservation None = new("", 0, 0);
}

/// <summary>
/// Per-team tokens-per-minute limit. The real token count is only known after the response,
/// so the request reserves an upper estimate (prompt estimate + max_tokens) before it is sent,
/// and the unused part is refunded afterwards. See ADR 0006.
/// </summary>
public sealed class TokenRateLimit(
    IRateLimiter limiter,
    IOptionsMonitor<RateLimitOptions> options,
    ILogger<TokenRateLimit> logger)
{
    /// <summary>Most completion tokens the request can produce: its max_tokens, or the configured default.</summary>
    public int MaxCompletionTokens(JsonObject chatRequest) =>
        Math.Max(0, ReadInt(chatRequest["max_completion_tokens"])
                    ?? ReadInt(chatRequest["max_tokens"])
                    ?? options.CurrentValue.DefaultMaxTokensEstimate);

    /// <summary>
    /// Reserves <paramref name="estimatedTokens"/> for the team. Returns the reservation, or null if the
    /// request was rejected; in that case the error response has already been written.
    /// </summary>
    public async Task<TokenReservation?> TryReserveAsync(HttpContext ctx, Team team, int estimatedTokens)
    {
        var tpm = team.TokensPerMinute ?? options.CurrentValue.TokensPerMinute;
        if (tpm <= 0)
        {
            return TokenReservation.None;
        }

        if (estimatedTokens > tpm)
        {
            // It could never fit, so a 429 would make clients retry forever.
            await OpenAiError.WriteAsync(ctx, StatusCodes.Status400BadRequest,
                $"This request may use up to {estimatedTokens} tokens, more than team '{team.Id}' is allowed per minute ({tpm}). " +
                "Lower max_tokens or shorten the prompt.",
                "invalid_request_error", "request_too_large");
            return null;
        }

        var key = $"rl:{{{team.Id}}}:tpm";
        RateLimitDecision decision;
        try
        {
            decision = await limiter.TryAcquireAsync(key, tpm, tpm / 60d, estimatedTokens);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token limiter unavailable; allowing request for team {TeamId}", team.Id);
            return TokenReservation.None;
        }

        ctx.Response.Headers["x-ratelimit-limit-tokens"] = tpm.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["x-ratelimit-remaining-tokens"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (decision.Allowed)
        {
            return new TokenReservation(key, tpm, estimatedTokens);
        }

        var retrySeconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        ctx.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
        await OpenAiError.WriteAsync(ctx, StatusCodes.Status429TooManyRequests,
            $"Token rate limit reached for team '{team.Id}': {tpm} tokens per minute. " +
            $"This request may use up to {estimatedTokens}. Try again in {retrySeconds}s.",
            "tokens", "rate_limit_exceeded");
        return null;
    }

    /// <summary>Refunds whatever was reserved but not used. Best effort: a failed refund only makes the limit stricter.</summary>
    public async Task SettleAsync(TokenReservation reservation, int actualTokens)
    {
        var unused = reservation.Reserved - actualTokens;
        if (reservation == TokenReservation.None || unused <= 0)
        {
            return;
        }

        try
        {
            await limiter.RefundAsync(reservation.Key, reservation.Capacity, unused);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not refund {Tokens} tokens to {Key}", unused, reservation.Key);
        }
    }

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var i) ? i : null;
}
