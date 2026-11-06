using LlmGateway.Admin;
using LlmGateway.Auth;
using LlmGateway.Caching;
using LlmGateway.Chat;
using LlmGateway.Config;
using LlmGateway.Data;
using LlmGateway.Limits;
using LlmGateway.Providers;
using LlmGateway.Usage;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var gatewaySection = builder.Configuration.GetSection(GatewayOptions.SectionName);
builder.Services.Configure<GatewayOptions>(gatewaySection);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();

// Storage: Postgres when a connection string is set, otherwise in-memory (tests, quick local runs).
var postgres = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(postgres))
{
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgres));
    builder.Services.AddSingleton<ITeamStore, PostgresTeamStore>();
    builder.Services.AddSingleton<IUsageStore, PostgresUsageStore>();
    builder.Services.AddHostedService<DatabaseInitializer>(); // registered first so it runs before bootstrap
}
else
{
    builder.Services.AddSingleton<ITeamStore, InMemoryTeamStore>();
    builder.Services.AddSingleton<IUsageStore, InMemoryUsageStore>();
}

builder.Services.AddHostedService<BootstrapService>();

// Rate limits: Redis when configured (shared by all instances), otherwise in-process.
builder.Services.Configure<RateLimitOptions>(gatewaySection.GetSection("RateLimits"));
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redis))
{
    // abortConnect=false: start even if Redis is down; the limiter fails open until it's back.
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(redis + ",abortConnect=false"));
    builder.Services.AddSingleton<IRateLimiter, RedisRateLimiter>();
    builder.Services.AddSingleton<IBudgetStore, RedisBudgetStore>();
    builder.Services.AddSingleton<IResponseCache, RedisResponseCache>();
    builder.Services.AddSingleton<ISemanticCache, RedisSemanticCache>();
}
else
{
    builder.Services.AddSingleton<IRateLimiter, InMemoryRateLimiter>();
    builder.Services.AddSingleton<IBudgetStore, InMemoryBudgetStore>();
    builder.Services.AddSingleton<IResponseCache, InMemoryResponseCache>();
    builder.Services.AddSingleton<ISemanticCache, InMemorySemanticCache>();
}

builder.Services.AddSingleton<TokenRateLimit>();
builder.Services.Configure<BudgetOptions>(gatewaySection.GetSection("Budgets"));
builder.Services.AddSingleton<BudgetGuard>();
builder.Services.Configure<CacheOptions>(gatewaySection.GetSection("Cache"));
builder.Services.AddSingleton<ResponseCaching>();
builder.Services.AddSingleton<MockEmbeddingProvider>();
builder.Services.AddSingleton<OpenAiEmbeddingProvider>();
builder.Services.AddSingleton<IEmbeddingProvider>(sp =>
    string.Equals(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CacheOptions>>().Value.Embeddings.Provider, "mock",
        StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<MockEmbeddingProvider>()
        : sp.GetRequiredService<OpenAiEmbeddingProvider>());

// Usage: request path -> bounded channel -> batched background writer.
builder.Services.AddSingleton<ChannelUsageRecorder>();
builder.Services.AddSingleton<IUsageRecorder>(sp => sp.GetRequiredService<ChannelUsageRecorder>());
builder.Services.AddHostedService<UsageWriterService>();

// One named HttpClient per HTTP provider, so each gets its own base URL, timeout and connection pool.
var gatewayOptions = gatewaySection.Get<GatewayOptions>() ?? new GatewayOptions();
foreach (var (name, provider) in gatewayOptions.Providers)
{
    if (!ProviderRegistry.UsesHttp(provider))
    {
        continue;
    }

    if (string.IsNullOrWhiteSpace(provider.BaseUrl))
    {
        throw new InvalidOperationException($"Provider '{name}' needs a BaseUrl.");
    }

    builder.Services.AddHttpClient(OpenAiCompatibleProvider.HttpClientName(name), client =>
    {
        client.BaseAddress = new Uri(provider.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(provider.TimeoutSeconds);
    });
}

builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<FallbackSender>();
builder.Services.Configure<CircuitBreakerOptions>(gatewaySection.GetSection("CircuitBreaker"));
builder.Services.AddSingleton<CircuitBreakers>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();
app.UseMiddleware<RateLimitMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/chat/completions", ChatCompletionsEndpoint.HandleAsync);

app.MapGet("/v1/models", (ProviderRegistry registry) => Results.Ok(new
{
    @object = "list",
    data = registry.Models.Select(m => new { id = m.Model, @object = "model", owned_by = m.Provider }),
}));

app.MapAdminEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program
{
}
