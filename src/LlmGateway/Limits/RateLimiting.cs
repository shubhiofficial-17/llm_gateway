using System.Collections.Concurrent;
using StackExchange.Redis;

namespace LlmGateway.Limits;

public sealed class RateLimitOptions
{
    /// <summary>Requests per minute allowed for each team. 0 turns the limit off.</summary>
    public int RequestsPerMinute { get; set; } = 60;
}

public sealed record RateLimitDecision(bool Allowed, int Remaining, TimeSpan RetryAfter);

public interface IRateLimiter
{
    /// <summary>
    /// Token bucket: holds up to <paramref name="capacity"/> tokens and refills at
    /// <paramref name="tokensPerSecond"/>. Takes <paramref name="cost"/> tokens if available.
    /// </summary>
    Task<RateLimitDecision> TryAcquireAsync(string key, int capacity, double tokensPerSecond, int cost);
}

/// <summary>Shared across all gateway instances. The check-and-take runs as one Lua script, so it's atomic.</summary>
public sealed class RedisRateLimiter(IConnectionMultiplexer redis) : IRateLimiter
{
    // StackExchange.Redis caches the script's SHA and sends EVALSHA after the first call.
    private static readonly string Script = EmbeddedScript.Read("token_bucket.lua");

    public async Task<RateLimitDecision> TryAcquireAsync(string key, int capacity, double tokensPerSecond, int cost)
    {
        var tokensPerMs = tokensPerSecond / 1000d;
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            [new RedisKey(key)],
            [capacity, tokensPerMs, cost]))!;

        return new RateLimitDecision(
            Allowed: (int)result[0] == 1,
            Remaining: (int)result[1],
            RetryAfter: TimeSpan.FromMilliseconds((long)result[2]));
    }
}

/// <summary>Single-instance fallback used when Redis isn't configured (tests, quick local runs).</summary>
public sealed class InMemoryRateLimiter(TimeProvider time) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public Task<RateLimitDecision> TryAcquireAsync(string key, int capacity, double tokensPerSecond, int cost)
    {
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(capacity, time.GetUtcNow()));
        lock (bucket)
        {
            var now = time.GetUtcNow();
            if (now > bucket.UpdatedAt)
            {
                bucket.Tokens = Math.Min(capacity, bucket.Tokens + (now - bucket.UpdatedAt).TotalSeconds * tokensPerSecond);
                bucket.UpdatedAt = now;
            }

            if (bucket.Tokens >= cost)
            {
                bucket.Tokens -= cost;
                return Task.FromResult(new RateLimitDecision(true, (int)Math.Floor(bucket.Tokens), TimeSpan.Zero));
            }

            var wait = TimeSpan.FromSeconds((cost - bucket.Tokens) / tokensPerSecond);
            return Task.FromResult(new RateLimitDecision(false, (int)Math.Floor(bucket.Tokens), wait));
        }
    }

    private sealed class Bucket(double tokens, DateTimeOffset updatedAt)
    {
        public double Tokens { get; set; } = tokens;
        public DateTimeOffset UpdatedAt { get; set; } = updatedAt;
    }
}

internal static class EmbeddedScript
{
    public static string Read(string name)
    {
        using var stream = typeof(EmbeddedScript).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded resource {name} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
