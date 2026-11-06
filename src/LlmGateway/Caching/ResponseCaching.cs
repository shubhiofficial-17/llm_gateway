using System.Text.Json.Nodes;
using LlmGateway.Auth;
using Microsoft.Extensions.Options;

namespace LlmGateway.Caching;

/// <summary>Result of a cache lookup. On a miss it carries what's needed to store the answer afterwards.</summary>
public sealed record CacheLookup(
    CachedCompletion? Hit,
    string Kind,
    double? Similarity,
    string ExactKey,
    string? SemanticBucket,
    float[]? Embedding);

/// <summary>
/// Exact-match cache first (ADR 0011), then, if enabled, the semantic cache (ADR 0012).
/// Every cache operation fails open: a broken cache only means a miss.
/// </summary>
public sealed class ResponseCaching(
    IResponseCache exact,
    ISemanticCache semantic,
    IEmbeddingProvider embeddings,
    IOptionsMonitor<CacheOptions> options,
    ILogger<ResponseCaching> logger)
{
    /// <summary>Non-streaming requests only. The x-gateway-cache header ("on" / "off") overrides the default.</summary>
    public bool IsRequested(HttpRequest request, bool stream) =>
        !stream && request.Headers["x-gateway-cache"].ToString().Trim().ToLowerInvariant() switch
        {
            "on" or "true" => true,
            "off" or "false" => false,
            _ => options.CurrentValue.DefaultOn,
        };

    public async Task<CacheLookup> LookupAsync(Team team, JsonObject request, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var exactKey = CacheKeys.For(team.Id, request);

        var hit = await TryAsync(() => exact.GetAsync(exactKey), "exact cache read");
        if (hit is not null)
        {
            return new CacheLookup(hit, "hit", null, exactKey, null, null);
        }

        if (!settings.SemanticEnabled || CacheKeys.SemanticBucket(team.Id, request) is not { } bucket)
        {
            return new CacheLookup(null, "miss", null, exactKey, null, null);
        }

        var embedding = await TryAsync(() => embeddings.EmbedAsync(bucket.Question, ct), "embedding");
        if (embedding is null)
        {
            return new CacheLookup(null, "miss", null, exactKey, null, null);
        }

        var match = await TryAsync(() => semantic.FindAsync(bucket.Bucket, embedding, settings.SimilarityThreshold), "semantic cache read");
        return match is null
            ? new CacheLookup(null, "miss", null, exactKey, bucket.Bucket, embedding)
            : new CacheLookup(match.Completion, "semantic-hit", match.Similarity, exactKey, bucket.Bucket, embedding);
    }

    public async Task StoreAsync(CacheLookup lookup, CachedCompletion completion)
    {
        var settings = options.CurrentValue;
        var ttl = TimeSpan.FromSeconds(settings.TtlSeconds);

        await TryAsync(async () =>
        {
            await exact.SetAsync(lookup.ExactKey, completion, ttl);
            return true;
        }, "exact cache write");

        if (lookup.SemanticBucket is not null && lookup.Embedding is not null)
        {
            await TryAsync(async () =>
            {
                await semantic.AddAsync(lookup.SemanticBucket, lookup.Embedding, completion, ttl, settings.MaxEntriesPerBucket);
                return true;
            }, "semantic cache write");
        }
    }

    private async Task<T?> TryAsync<T>(Func<Task<T?>> action, string what) where T : class
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Response cache: {What} failed; continuing without the cache", what);
            return null;
        }
    }

    private async Task TryAsync(Func<Task<bool>> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Response cache: {What} failed", what);
        }
    }
}
