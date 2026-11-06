using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace LlmGateway.Caching;

public sealed class CacheOptions
{
    /// <summary>Cache requests that don't send an x-gateway-cache header. Off by default: caching is opt-in.</summary>
    public bool DefaultOn { get; set; }

    public int TtlSeconds { get; set; } = 3600;

    /// <summary>Also answer rephrased questions from the cache, by comparing embeddings.</summary>
    public bool SemanticEnabled { get; set; }

    /// <summary>Cosine similarity a cached question needs to count as the same question.</summary>
    public double SimilarityThreshold { get; set; } = 0.95;

    /// <summary>Questions kept per bucket (same model, system prompt and history). Oldest are dropped.</summary>
    public int MaxEntriesPerBucket { get; set; } = 500;

    public EmbeddingOptions Embeddings { get; set; } = new();
}

public sealed class EmbeddingOptions
{
    /// <summary>"mock", or the name of an OpenAI-compatible provider that serves /embeddings.</summary>
    public string Provider { get; set; } = "mock";

    public string Model { get; set; } = "text-embedding-3-small";
}

/// <summary>A stored answer, plus what it cost the first time (counted as saved on every hit).</summary>
public sealed record CachedCompletion(string Body, string Model, int PromptTokens, int CompletionTokens, long CostMicros);

public interface IResponseCache
{
    Task<CachedCompletion?> GetAsync(string key);

    Task SetAsync(string key, CachedCompletion value, TimeSpan ttl);
}

public static class CacheKeys
{
    // Fields that change how a response is delivered, not what it says.
    private static readonly HashSet<string> Ignored = ["stream", "stream_options", "user"];

    /// <summary>
    /// Key for a team's request. The team id is part of the key so one team can never be served
    /// another team's answer. JSON keys are sorted so field order doesn't matter.
    /// </summary>
    public static string For(string teamId, JsonObject request)
    {
        var canonical = Canonicalize(request, topLevel: true)!.ToJsonString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"cache:{{{teamId}}}:{hash}";
    }

    /// <summary>
    /// Bucket for semantic lookups: the request with its last user message removed. Only requests with
    /// the same model, parameters, system prompt and earlier turns share a bucket, so a match can only
    /// differ in how the final question was phrased. Returns null if the request doesn't end in a user message.
    /// </summary>
    public static (string Bucket, string Question)? SemanticBucket(string teamId, JsonObject request)
    {
        if (request["messages"] is not JsonArray { Count: > 0 } messages
            || messages[^1] is not JsonObject last
            || last["role"]?.GetValue<string>() != "user")
        {
            return null;
        }

        var question = Usage.TokenEstimator.ContentText(last["content"]);
        var context = (JsonObject)request.DeepClone();
        context["messages"]!.AsArray().RemoveAt(messages.Count - 1);

        var canonical = Canonicalize(context, topLevel: true)!.ToJsonString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return ($"scache:{{{teamId}}}:{hash}", question);
    }

    private static JsonNode? Canonicalize(JsonNode? node, bool topLevel = false) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .Where(p => !(topLevel && Ignored.Contains(p.Key)))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray array => new JsonArray(array.Select(item => Canonicalize(item)).ToArray()),
        _ => node?.DeepClone(),
    };
}

public sealed class RedisResponseCache(IConnectionMultiplexer redis) : IResponseCache
{
    public async Task<CachedCompletion?> GetAsync(string key)
    {
        var value = await redis.GetDatabase().StringGetAsync(key);
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<CachedCompletion>(value.ToString());
    }

    public Task SetAsync(string key, CachedCompletion value, TimeSpan ttl) =>
        redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value), ttl);
}

public sealed class InMemoryResponseCache(IMemoryCache cache) : IResponseCache
{
    public Task<CachedCompletion?> GetAsync(string key) => Task.FromResult(cache.Get<CachedCompletion>(key));

    public Task SetAsync(string key, CachedCompletion value, TimeSpan ttl)
    {
        cache.Set(key, value, ttl);
        return Task.CompletedTask;
    }
}
