using Microsoft.Extensions.Caching.Memory;

namespace LlmGateway.Auth;

/// <summary>
/// Authenticates /v1/* requests with "Authorization: Bearer &lt;key&gt;" and attaches the caller's team.
/// Lookups are cached so the database is not hit on every request; unknown keys are cached briefly
/// so a client retrying with a bad key cannot hammer the database.
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan ValidKeyTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InvalidKeyTtl = TimeSpan.FromSeconds(5);

    public async Task InvokeAsync(HttpContext ctx, ITeamStore store, IMemoryCache cache)
    {
        if (!ctx.Request.Path.StartsWithSegments("/v1"))
        {
            await next(ctx);
            return;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await OpenAiError.WriteAsync(ctx, StatusCodes.Status401Unauthorized,
                "Missing API key. Send it as 'Authorization: Bearer <key>'.", "invalid_request_error", "missing_api_key");
            return;
        }

        var apiKey = header["Bearer ".Length..].Trim();
        var keyHash = ApiKeys.Hash(apiKey);
        var cacheKey = "apikey:" + Convert.ToHexString(keyHash);

        var team = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            var found = await store.FindTeamByKeyHashAsync(keyHash, ctx.RequestAborted);
            entry.AbsoluteExpirationRelativeToNow = found is null ? InvalidKeyTtl : ValidKeyTtl;
            return found;
        });

        if (team is null)
        {
            await OpenAiError.WriteAsync(ctx, StatusCodes.Status401Unauthorized,
                "Invalid API key.", "invalid_request_error", "invalid_api_key");
            return;
        }

        ctx.SetTeam(team);
        await next(ctx);
    }
}

public static class TeamHttpContextExtensions
{
    private const string ItemKey = "gateway.team";

    public static void SetTeam(this HttpContext ctx, Team team) => ctx.Items[ItemKey] = team;

    public static Team GetTeam(this HttpContext ctx) =>
        ctx.Items[ItemKey] as Team ?? throw new InvalidOperationException("No authenticated team on this request.");
}
