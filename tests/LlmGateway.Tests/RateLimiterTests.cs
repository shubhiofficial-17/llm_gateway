using LlmGateway.Limits;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace LlmGateway.Tests;

public sealed class InMemoryRateLimiterTests
{
    [Fact]
    public async Task Allows_a_burst_up_to_capacity_then_refills_over_time()
    {
        var clock = new FakeTimeProvider();
        var limiter = new InMemoryRateLimiter(clock);

        // capacity 3, refills 3 per minute (one every 20s)
        Assert.True((await limiter.TryAcquireAsync("k", 3, 3 / 60d, 1)).Allowed);
        Assert.True((await limiter.TryAcquireAsync("k", 3, 3 / 60d, 1)).Allowed);
        Assert.True((await limiter.TryAcquireAsync("k", 3, 3 / 60d, 1)).Allowed);

        var denied = await limiter.TryAcquireAsync("k", 3, 3 / 60d, 1);
        Assert.False(denied.Allowed);
        Assert.InRange(denied.RetryAfter, TimeSpan.FromSeconds(19.9), TimeSpan.FromSeconds(20.1));

        clock.Advance(TimeSpan.FromSeconds(21));
        Assert.True((await limiter.TryAcquireAsync("k", 3, 3 / 60d, 1)).Allowed);
        Assert.False((await limiter.TryAcquireAsync("k", 3, 3 / 60d, 1)).Allowed);
    }

    [Fact]
    public async Task Refund_returns_tokens_up_to_capacity()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider());

        Assert.True((await limiter.TryAcquireAsync("k", 100, 0.001, 95)).Allowed);
        await limiter.RefundAsync("k", 100, 85);
        var after = await limiter.TryAcquireAsync("k", 100, 0.001, 95);

        Assert.False(after.Allowed);
        Assert.Equal(90, after.Remaining);

        await limiter.RefundAsync("k", 100, 1_000);
        Assert.Equal(5, (await limiter.TryAcquireAsync("k", 100, 0.001, 95)).Remaining);
    }

    [Fact]
    public async Task Keys_have_separate_buckets()
    {
        var limiter = new InMemoryRateLimiter(new FakeTimeProvider());

        Assert.True((await limiter.TryAcquireAsync("a", 1, 1, 1)).Allowed);
        Assert.False((await limiter.TryAcquireAsync("a", 1, 1, 1)).Allowed);
        Assert.True((await limiter.TryAcquireAsync("b", 1, 1, 1)).Allowed);
    }
}

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7-alpine").Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}

public sealed class RedisRateLimiterTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private static string NewKey() => $"rl:{{test-{Guid.NewGuid():N}}}:rpm";

    [Fact]
    public async Task Denies_after_capacity_and_reports_when_to_retry()
    {
        var limiter = new RedisRateLimiter(redis.Connection);
        var key = NewKey();

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await limiter.TryAcquireAsync(key, 3, 3 / 60d, 1)).Allowed);
        }

        var denied = await limiter.TryAcquireAsync(key, 3, 3 / 60d, 1);
        Assert.False(denied.Allowed);
        Assert.Equal(0, denied.Remaining);
        Assert.InRange(denied.RetryAfter, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Concurrent_requests_never_exceed_capacity()
    {
        var limiter = new RedisRateLimiter(redis.Connection);
        var key = NewKey();

        // 200 requests race for 10 tokens; refill is slow enough to be irrelevant during the test.
        var results = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(_ => limiter.TryAcquireAsync(key, 10, 0.0001, 1)));

        Assert.Equal(10, results.Count(r => r.Allowed));
    }

    [Fact]
    public async Task Refund_returns_tokens_up_to_capacity()
    {
        var limiter = new RedisRateLimiter(redis.Connection);
        var key = NewKey();

        Assert.True((await limiter.TryAcquireAsync(key, 100, 0.0001, 95)).Allowed);
        await limiter.RefundAsync(key, 100, 85);
        var after = await limiter.TryAcquireAsync(key, 100, 0.0001, 95);

        Assert.False(after.Allowed);
        Assert.Equal(90, after.Remaining);

        await limiter.RefundAsync(key, 100, 1_000);
        Assert.Equal(5, (await limiter.TryAcquireAsync(key, 100, 0.0001, 95)).Remaining);
    }

    [Fact]
    public async Task Idle_buckets_expire()
    {
        var limiter = new RedisRateLimiter(redis.Connection);
        var key = NewKey();

        await limiter.TryAcquireAsync(key, 60, 1, 1);

        var ttl = await redis.Connection.GetDatabase().KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(61));
    }
}
