using LlmGateway.Auth;
using LlmGateway.Config;
using Microsoft.Extensions.Options;
using Npgsql;

namespace LlmGateway.Data;

/// <summary>Applies the idempotent schema on startup, waiting for Postgres to accept connections.</summary>
public sealed class DatabaseInitializer(NpgsqlDataSource db, ILogger<DatabaseInitializer> logger) : IHostedService
{
    private const int MaxAttempts = 30;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var schema = await ReadSchemaAsync();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = db.CreateCommand(schema);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                logger.LogInformation("Database schema is up to date");
                return;
            }
            catch (NpgsqlException ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}", attempt, MaxAttempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static async Task<string> ReadSchemaAsync()
    {
        await using var stream = typeof(DatabaseInitializer).Assembly.GetManifestResourceStream("schema.sql")
                                 ?? throw new InvalidOperationException("Embedded schema.sql not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

/// <summary>Creates the demo team and API key from Gateway:Bootstrap, if configured.</summary>
public sealed class BootstrapService(ITeamStore teams, IOptions<GatewayOptions> options, ILogger<BootstrapService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrap = options.Value.Bootstrap;
        if (string.IsNullOrWhiteSpace(bootstrap.TeamId) || string.IsNullOrWhiteSpace(bootstrap.ApiKey))
        {
            return;
        }

        await teams.UpsertTeamAsync(new Team(bootstrap.TeamId, bootstrap.TeamName ?? bootstrap.TeamId), cancellationToken);
        await teams.AddKeyAsync(bootstrap.TeamId, ApiKeys.Hash(bootstrap.ApiKey), "bootstrap", cancellationToken);
        logger.LogInformation("Bootstrap team {TeamId} is ready", bootstrap.TeamId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
