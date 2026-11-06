using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LlmGateway.Auth;
using LlmGateway.Config;
using LlmGateway.Usage;
using Microsoft.Extensions.Options;

namespace LlmGateway.Admin;

public sealed record CreateTeamRequest(string Id, string Name);

public sealed record CreateKeyRequest(string? Label);

/// <summary>Null clears an override so the team uses the gateway default. 0 means unlimited.</summary>
public sealed record SetLimitsRequest(int? RequestsPerMinute);

public sealed record CreateKeyResponse(string TeamId, string ApiKey, string? Label, string Note);

public static partial class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminKeyFilter>();

        admin.MapPost("/teams", async (CreateTeamRequest request, ITeamStore teams, CancellationToken ct) =>
        {
            if (!TeamIdPattern().IsMatch(request.Id) || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["id"] = ["Use 2-63 lowercase letters, digits or hyphens, starting with a letter or digit."],
                    ["name"] = ["Required."],
                });
            }

            var team = new Team(request.Id, request.Name.Trim());
            await teams.UpsertTeamAsync(team, ct);
            return Results.Ok(team);
        });

        admin.MapGet("/teams/{teamId}", async (string teamId, ITeamStore teams, CancellationToken ct) =>
            await teams.GetTeamAsync(teamId, ct) is { } team
                ? Results.Ok(team)
                : Results.NotFound(new { error = $"Team '{teamId}' not found." }));

        // Changes reach requests within a minute: team lookups are cached per API key for 60s.
        admin.MapPut("/teams/{teamId}/limits", async (string teamId, SetLimitsRequest request, ITeamStore teams, CancellationToken ct) =>
        {
            if (request.RequestsPerMinute is < 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["requestsPerMinute"] = ["Must be 0 (unlimited) or more, or null for the default."],
                });
            }

            return await teams.SetLimitsAsync(teamId, request.RequestsPerMinute, ct) is { } team
                ? Results.Ok(team)
                : Results.NotFound(new { error = $"Team '{teamId}' not found." });
        });

        admin.MapPost("/teams/{teamId}/keys", async (string teamId, CreateKeyRequest? request, ITeamStore teams, CancellationToken ct) =>
        {
            if (await teams.GetTeamAsync(teamId, ct) is null)
            {
                return Results.NotFound(new { error = $"Team '{teamId}' not found." });
            }

            var apiKey = ApiKeys.Generate();
            await teams.AddKeyAsync(teamId, ApiKeys.Hash(apiKey), request?.Label, ct);
            return Results.Ok(new CreateKeyResponse(teamId, apiKey, request?.Label,
                "Store this key now. Only its hash is kept, so it cannot be shown again."));
        });

        admin.MapGet("/teams/{teamId}/usage", async (
            string teamId, DateTimeOffset? from, DateTimeOffset? to,
            IUsageStore usage, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow();
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            return Results.Ok(await usage.GetSummaryAsync(teamId, from ?? monthStart, to ?? now.AddSeconds(1), ct));
        });

        return admin;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex TeamIdPattern();
}

/// <summary>Requires X-Admin-Key. Admin endpoints are disabled entirely when no admin key is configured.</summary>
public sealed class AdminKeyFilter(IOptions<GatewayOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = options.Value.AdminKey;
        if (string.IsNullOrEmpty(expected))
        {
            return Results.NotFound();
        }

        var provided = context.HttpContext.Request.Headers["X-Admin-Key"].ToString();

        // Compare fixed-length hashes in constant time so response timing does not leak the key.
        var match = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));

        return match ? await next(context) : Results.Unauthorized();
    }
}
