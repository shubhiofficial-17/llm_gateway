using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;

namespace LlmGateway.Usage;

public interface IUsageStore
{
    Task WriteBatchAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct);

    Task<UsageSummary> GetSummaryAsync(string teamId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}

public sealed class PostgresUsageStore(NpgsqlDataSource db) : IUsageStore
{
    public async Task WriteBatchAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return;
        }

        // Binary COPY: one round trip for the whole batch.
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY usage_events (request_id, team_id, model, provider, prompt_tokens, completion_tokens, " +
            "cost_micros, status_code, latency_ms, streamed, usage_estimated, created_at) FROM STDIN (FORMAT BINARY)",
            ct);

        foreach (var e in events)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(e.RequestId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.TeamId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Model, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.Provider, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(e.PromptTokens, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(e.CompletionTokens, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(e.CostMicros, NpgsqlDbType.Bigint, ct);
            await writer.WriteAsync(e.StatusCode, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(e.LatencyMs, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(e.Streamed, NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(e.UsageEstimated, NpgsqlDbType.Boolean, ct);
            await writer.WriteAsync(e.CreatedAt.UtcDateTime, NpgsqlDbType.TimestampTz, ct);
        }

        await writer.CompleteAsync(ct);
    }

    public async Task<UsageSummary> GetSummaryAsync(string teamId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT model,
                   COUNT(*)::bigint,
                   COALESCE(SUM(prompt_tokens), 0)::bigint,
                   COALESCE(SUM(completion_tokens), 0)::bigint,
                   COALESCE(SUM(cost_micros), 0)::bigint
            FROM usage_events
            WHERE team_id = $1 AND created_at >= $2 AND created_at < $3
            GROUP BY model
            ORDER BY 5 DESC, model
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = teamId, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = from.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = to.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });

        var rows = new List<ModelUsage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ModelUsage(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return UsageSummaries.Build(teamId, from, to, rows);
    }
}

/// <summary>Used when no Postgres connection string is configured (tests, quick local runs).</summary>
public sealed class InMemoryUsageStore : IUsageStore
{
    private readonly ConcurrentQueue<UsageEvent> _events = new();

    public IReadOnlyCollection<UsageEvent> Events => _events.ToArray();

    public Task WriteBatchAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            _events.Enqueue(e);
        }

        return Task.CompletedTask;
    }

    public Task<UsageSummary> GetSummaryAsync(string teamId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = _events
            .Where(e => e.TeamId == teamId && e.CreatedAt >= from && e.CreatedAt < to)
            .GroupBy(e => e.Model)
            .Select(g => new ModelUsage(
                g.Key,
                g.LongCount(),
                g.Sum(e => (long)e.PromptTokens),
                g.Sum(e => (long)e.CompletionTokens),
                g.Sum(e => e.CostMicros)))
            .OrderByDescending(m => m.CostMicros)
            .ThenBy(m => m.Model, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(UsageSummaries.Build(teamId, from, to, rows));
    }
}

internal static class UsageSummaries
{
    public static UsageSummary Build(string teamId, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<ModelUsage> rows)
    {
        var cost = rows.Sum(r => r.CostMicros);
        return new UsageSummary(teamId, from, to, rows.Sum(r => r.Requests), cost, Pricing.MicrosToUsd(cost), rows);
    }
}
