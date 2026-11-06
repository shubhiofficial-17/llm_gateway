using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace LlmGateway.Auth;

public sealed record Team(string Id, string Name);

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
}

public sealed class PostgresTeamStore(NpgsqlDataSource db) : ITeamStore
{
    public async Task<Team?> FindTeamByKeyHashAsync(byte[] keyHash, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT t.id, t.name
            FROM api_keys k
            JOIN teams t ON t.id = k.team_id
            WHERE k.key_hash = $1 AND k.revoked_at IS NULL
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = keyHash, NpgsqlDbType = NpgsqlDbType.Bytea });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new Team(reader.GetString(0), reader.GetString(1)) : null;
    }

    public async Task<Team?> GetTeamAsync(string teamId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT id, name FROM teams WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = teamId, NpgsqlDbType = NpgsqlDbType.Text });

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new Team(reader.GetString(0), reader.GetString(1)) : null;
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
        _teams[team.Id] = team;
        return Task.CompletedTask;
    }

    public Task AddKeyAsync(string teamId, byte[] keyHash, string? label, CancellationToken ct)
    {
        _keyHashToTeam.TryAdd(Convert.ToHexString(keyHash), teamId);
        return Task.CompletedTask;
    }
}
