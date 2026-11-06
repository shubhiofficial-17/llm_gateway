using LlmGateway.Auth;
using LlmGateway.Data;
using LlmGateway.Usage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LlmGateway.Tests;

/// <summary>Starts a real Postgres in Docker once for all tests in this class.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await new DatabaseInitializer(DataSource, NullLogger<DatabaseInitializer>.Instance).StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

public sealed class PostgresStoreTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Schema_can_be_applied_repeatedly()
    {
        var initializer = new DatabaseInitializer(db.DataSource, NullLogger<DatabaseInitializer>.Instance);

        await initializer.StartAsync(CancellationToken.None);
        await initializer.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Api_key_resolves_to_its_team_and_only_the_hash_is_stored()
    {
        var store = new PostgresTeamStore(db.DataSource);
        var teamId = "team-" + Guid.NewGuid().ToString("N")[..8];
        var apiKey = ApiKeys.Generate();

        await store.UpsertTeamAsync(new Team(teamId, "Search"), CancellationToken.None);
        await store.AddKeyAsync(teamId, ApiKeys.Hash(apiKey), "test", CancellationToken.None);

        var found = await store.FindTeamByKeyHashAsync(ApiKeys.Hash(apiKey), CancellationToken.None);
        Assert.Equal(new Team(teamId, "Search"), found);
        Assert.Null(await store.FindTeamByKeyHashAsync(ApiKeys.Hash(apiKey + "x"), CancellationToken.None));

        // Stored value is the 32-byte SHA-256 digest, not the key itself.
        await using var cmd = db.DataSource.CreateCommand("SELECT octet_length(key_hash) FROM api_keys WHERE team_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = teamId });
        Assert.Equal(32, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Usage_batch_is_written_with_copy_and_summarised_per_model()
    {
        var store = new PostgresUsageStore(db.DataSource);
        var teamId = "team-" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;

        await store.WriteBatchAsync(
        [
            Event(teamId, "gpt-4o", 100, 50, 750, now),
            Event(teamId, "gpt-4o", 200, 100, 1500, now),
            Event(teamId, "gpt-4o-mini", 1000, 1000, 750, now),
            Event("other-team", "gpt-4o", 1, 1, 999_999, now),
            Event(teamId, "gpt-4o", 1, 1, 999_999, now.AddDays(-60)),
        ], CancellationToken.None);

        var summary = await store.GetSummaryAsync(teamId, now.AddDays(-1), now.AddDays(1), CancellationToken.None);

        Assert.Equal(3, summary.Requests);
        Assert.Equal(3000, summary.CostMicros);
        Assert.Equal(0.003m, summary.CostUsd);
        var gpt4o = Assert.Single(summary.ByModel, m => m.Model == "gpt-4o");
        Assert.Equal(new ModelUsage("gpt-4o", 2, 300, 150, 2250), gpt4o);
    }

    private static UsageEvent Event(string teamId, string model, int prompt, int completion, long cost, DateTimeOffset at) =>
        new("req_" + Guid.NewGuid().ToString("N"), teamId, model, "openai", prompt, completion, cost, 200, 42, false, false, at);
}
