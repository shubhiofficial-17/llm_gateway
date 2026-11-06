using System.Runtime.InteropServices;
using System.Text.Json;
using StackExchange.Redis;

namespace LlmGateway.Caching;

public sealed record SemanticMatch(CachedCompletion Completion, double Similarity);

/// <summary>
/// Stores answers with the embedding of the question that produced them, in buckets of requests that
/// differ only in their last user message. Lookups compare embeddings within one bucket. See ADR 0012.
/// </summary>
public interface ISemanticCache
{
    Task<SemanticMatch?> FindAsync(string bucket, float[] embedding, double threshold);

    Task AddAsync(string bucket, float[] embedding, CachedCompletion completion, TimeSpan ttl, int maxEntries);
}

internal sealed record SemanticEntry(string Embedding, CachedCompletion Completion)
{
    public static SemanticEntry Create(float[] embedding, CachedCompletion completion) =>
        new(Convert.ToBase64String(MemoryMarshal.AsBytes(embedding.AsSpan())), completion);

    public float[] Vector() => MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(Embedding).AsSpan()).ToArray();
}

internal static class SemanticSearch
{
    public static SemanticMatch? Best(IEnumerable<SemanticEntry> entries, float[] embedding, double threshold)
    {
        SemanticMatch? best = null;
        foreach (var entry in entries)
        {
            var similarity = Vectors.Cosine(entry.Vector(), embedding);
            if (similarity >= threshold && (best is null || similarity > best.Similarity))
            {
                best = new SemanticMatch(entry.Completion, similarity);
            }
        }

        return best;
    }
}

/// <summary>Bucket = Redis list, newest first, trimmed to maxEntries. Scanned linearly on lookup.</summary>
public sealed class RedisSemanticCache(IConnectionMultiplexer redis) : ISemanticCache
{
    public async Task<SemanticMatch?> FindAsync(string bucket, float[] embedding, double threshold)
    {
        var values = await redis.GetDatabase().ListRangeAsync(bucket);
        var entries = values
            .Where(v => !v.IsNullOrEmpty)
            .Select(v => JsonSerializer.Deserialize<SemanticEntry>(v.ToString())!);
        return SemanticSearch.Best(entries, embedding, threshold);
    }

    public async Task AddAsync(string bucket, float[] embedding, CachedCompletion completion, TimeSpan ttl, int maxEntries)
    {
        var db = redis.GetDatabase();
        var batch = db.CreateTransaction();
        _ = batch.ListLeftPushAsync(bucket, JsonSerializer.Serialize(SemanticEntry.Create(embedding, completion)));
        _ = batch.ListTrimAsync(bucket, 0, maxEntries - 1);
        _ = batch.KeyExpireAsync(bucket, ttl);
        await batch.ExecuteAsync();
    }
}

public sealed class InMemorySemanticCache : ISemanticCache
{
    private readonly Dictionary<string, LinkedList<SemanticEntry>> _buckets = new();

    public Task<SemanticMatch?> FindAsync(string bucket, float[] embedding, double threshold)
    {
        lock (_buckets)
        {
            return Task.FromResult(_buckets.TryGetValue(bucket, out var entries)
                ? SemanticSearch.Best(entries.ToList(), embedding, threshold)
                : null);
        }
    }

    public Task AddAsync(string bucket, float[] embedding, CachedCompletion completion, TimeSpan ttl, int maxEntries)
    {
        lock (_buckets)
        {
            if (!_buckets.TryGetValue(bucket, out var entries))
            {
                _buckets[bucket] = entries = new LinkedList<SemanticEntry>();
            }

            entries.AddFirst(SemanticEntry.Create(embedding, completion));
            while (entries.Count > maxEntries)
            {
                entries.RemoveLast();
            }
        }

        return Task.CompletedTask;
    }
}
