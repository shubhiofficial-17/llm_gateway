using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace LlmGateway.Auth;

/// <param name="RequestsPerMinute">Per-team override; null means the gateway default.</param>
/// <param name="TokensPerMinute">Per-team override; null means the gateway default.</param>
/// <param name="MonthlyBudgetMicros">Per-team override in micro-USD; null means the gateway default.</param>
public sealed record Team(
    string Id,
    string Name,
    int? RequestsPerMinute = null,
    int? TokensPerMinute = null,
    long? MonthlyBudgetMicros = null)
{
    public TeamLimits Limits => new(RequestsPerMinute, TokensPerMinute, MonthlyBudgetMicros);
}

/// <summary>Per-team overrides. Null fields fall back to the gateway defaults.</summary>
public sealed record TeamLimits(int? RequestsPerMinute, int? TokensPerMinute, long? MonthlyBudgetMicros);

public static class ApiKeys
{
    public const string Prefix = "gw_";

    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Prefix + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>SHA-256 is enough here: keys are 256-bit random values, so no salting or slow hashing is needed.</summary>
    public static byte[] Hash(string apiKey) => SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
}

public interface ITeamStore
{
    Task<Team?> FindTeamByKeyHashAsync(byte[] keyHash, CancellationToken ct);

    Task<Team?> GetTeamAsync(string teamId, CancellationToken ct);

    Task UpsertTeamAsync(Team team, CancellationToken ct);

    Task AddKeyAsync(string teamId, byte[] keyHash, string? label, CancellationToken ct);

    /// <summary>Sets the team's limit overrides. Returns the updated team, or null if it doesn't exist.</summary>
    Task<Team?> SetLimitsAsync(string teamId, TeamLimits limits, CancellationToken ct);
}

public sealed class PostgresTeamStore(NpgsqlDataSource db) : ITeamStore
{
    public async Task<Team?> FindTeamByKeyHashAsync(byte[] keyHash, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT t.id, t.name, t.requests_per_minute, t.tokens_per_minute, t.monthly_budget_micros
            FROM api_keys k
            JOIN teams t ON t.id = k.team_id
            WHERE k.key_hash = $1 AND k.revoked_at IS NULL
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = keyHash, NpgsqlDbType = NpgsqlDbType.Bytea });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTeam(reader) : null;
    }

    public async Task<Team?> GetTeamAsync(string teamId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id, name, requests_per_minute, tokens_per_minute, monthly_budget_micros FROM teams WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = teamId, NpgsqlDbType = NpgsqlDbType.Text });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTeam(reader) : null;
    }

    public async Task UpsertTeamAsync(Team team, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO teams (id, name) VALUES ($1, $2) ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name");
        cmd.Parameters.Add(new NpgsqlParameter { Value = team.Id, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = team.Name, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddKeyAsync(string teamId, byte[] keyHash, string? label, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO api_keys (key_hash, team_id, label) VALUES ($1, $2, $3) ON CONFLICT (key_hash) DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter { Value = keyHash, NpgsqlDbType = NpgsqlDbType.Bytea });
        cmd.Parameters.Add(new NpgsqlParameter { Value = teamId, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)label ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Team?> SetLimitsAsync(string teamId, TeamLimits limits, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            UPDATE teams SET requests_per_minute = $2, tokens_per_minute = $3, monthly_budget_micros = $4
            WHERE id = $1
            RETURNING id, name, requests_per_minute, tokens_per_minute, monthly_budget_micros
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = teamId, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)limits.RequestsPerMinute ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)limits.TokensPerMinute ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)limits.MonthlyBudgetMicros ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadTeam(reader) : null;
    }

    private static Team ReadTeam(NpgsqlDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4));
}

/// <summary>Used when no Postgres connection string is configured (tests, quick local runs).</summary>
public sealed class InMemoryTeamStore : ITeamStore
{
    private readonly ConcurrentDictionary<string, Team> _teams = new();
    private readonly ConcurrentDictionary<string, string> _keyHashToTeam = new();

    public Task<Team?> FindTeamByKeyHashAsync(byte[] keyHash, CancellationToken ct)
    {
        var team = _keyHashToTeam.TryGetValue(Convert.ToHexString(keyHash), out var teamId)
            ? _teams.GetValueOrDefault(teamId)
            : null;
        return Task.FromResult(team);
    }

    public Task<Team?> GetTeamAsync(string teamId, CancellationToken ct) =>
        Task.FromResult(_teams.GetValueOrDefault(teamId));

    public Task UpsertTeamAsync(Team team, CancellationToken ct)
    {
        // Like the Postgres store, an upsert only changes the name; limits are kept.
        _teams.AddOrUpdate(team.Id, team, (_, existing) => existing with { Name = team.Name });
        return Task.CompletedTask;
    }

    public Task<Team?> SetLimitsAsync(string teamId, TeamLimits limits, CancellationToken ct)
    {
        while (_teams.TryGetValue(teamId, out var existing))
        {
            var updated = existing with
            {
                RequestsPerMinute = limits.RequestsPerMinute,
                TokensPerMinute = limits.TokensPerMinute,
                MonthlyBudgetMicros = limits.MonthlyBudgetMicros,
            };
            if (_teams.TryUpdate(teamId, updated, existing))
            {
                return Task.FromResult<Team?>(updated);
            }
        }

        return Task.FromResult<Team?>(null);
    }

    public Task AddKeyAsync(string teamId, byte[] keyHash, string? label, CancellationToken ct)
    {
        _keyHashToTeam.TryAdd(Convert.ToHexString(keyHash), teamId);
        return Task.CompletedTask;
    }
}
