using LlmGateway.Limits;
using Microsoft.Extensions.Time.Testing;

namespace LlmGateway.Tests;

/// <summary>The same behaviour is required of the Redis and in-memory budget stores.</summary>
public abstract class BudgetStoreContract
{
    protected abstract IBudgetStore Store { get; }

    /// <summary>Lets reservations with the given lifetime expire.</summary>
    protected abstract Task WaitPastAsync(TimeSpan lifetime);

    private static readonly TimeSpan Long = TimeSpan.FromMinutes(10);

    private static string NewTeam() => "team-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Asks_to_be_seeded_before_the_first_reservation_of_the_month()
    {
        var team = NewTeam();

        Assert.Equal(BudgetStatus.NeedsSeed, (await Store.TryReserveAsync(team, "2026-11", 100, 10, "a", Long)).Status);

        await Store.SeedAsync(team, "2026-11", 85);
        var decision = await Store.TryReserveAsync(team, "2026-11", 100, 10, "a", Long);
        Assert.Equal(new BudgetDecision(BudgetStatus.Reserved, 5), decision);
        Assert.Equal(BudgetStatus.OverBudget, (await Store.TryReserveAsync(team, "2026-11", 100, 10, "b", Long)).Status);

        // Seeding again never overwrites the running total.
        await Store.SeedAsync(team, "2026-11", 0);
        Assert.Equal(BudgetStatus.OverBudget, (await Store.TryReserveAsync(team, "2026-11", 100, 10, "c", Long)).Status);
    }

    [Fact]
    public async Task Settling_replaces_the_reservation_with_the_real_cost()
    {
        var team = NewTeam();
        await Store.SeedAsync(team, "2026-11", 0);

        await Store.TryReserveAsync(team, "2026-11", 100, 60, "a", Long);
        Assert.Equal(BudgetStatus.OverBudget, (await Store.TryReserveAsync(team, "2026-11", 100, 60, "b", Long)).Status);

        await Store.SettleAsync(team, "2026-11", "a", 60, 15);

        Assert.Equal(new BudgetDecision(BudgetStatus.Reserved, 25), await Store.TryReserveAsync(team, "2026-11", 100, 60, "c", Long));
    }

    [Fact]
    public async Task Abandoned_reservations_expire_and_late_settles_are_not_double_counted()
    {
        var team = NewTeam();
        var shortLife = TimeSpan.FromMilliseconds(50);
        await Store.SeedAsync(team, "2026-11", 0);

        await Store.TryReserveAsync(team, "2026-11", 100, 90, "crashed", shortLife);
        await WaitPastAsync(shortLife);

        // The abandoned 90 has been released.
        Assert.Equal(new BudgetDecision(BudgetStatus.Reserved, 50), await Store.TryReserveAsync(team, "2026-11", 100, 50, "next", Long));

        // A settle that arrives after expiry adds the real cost but doesn't release the 90 again.
        await Store.SettleAsync(team, "2026-11", "crashed", 90, 20);
        Assert.Equal(new BudgetDecision(BudgetStatus.OverBudget, 30), await Store.TryReserveAsync(team, "2026-11", 100, 40, "x", Long));
    }

    [Fact]
    public async Task Months_are_separate()
    {
        var team = NewTeam();
        await Store.SeedAsync(team, "2026-10", 100);
        await Store.SeedAsync(team, "2026-11", 0);

        Assert.Equal(BudgetStatus.OverBudget, (await Store.TryReserveAsync(team, "2026-10", 100, 1, "a", Long)).Status);
        Assert.Equal(BudgetStatus.Reserved, (await Store.TryReserveAsync(team, "2026-11", 100, 1, "b", Long)).Status);
    }

    [Fact]
    public async Task Parallel_requests_never_overspend()
    {
        var team = NewTeam();
        await Store.SeedAsync(team, "2026-11", 0);

        // 500 requests of 2 micro-USD race for a budget of 100.
        var decisions = await Task.WhenAll(Enumerable.Range(0, 500)
            .Select(i => Store.TryReserveAsync(team, "2026-11", 100, 2, $"r{i}", Long)));

        Assert.Equal(50, decisions.Count(d => d.Status == BudgetStatus.Reserved));
    }
}

public sealed class InMemoryBudgetStoreTests : BudgetStoreContract
{
    private readonly FakeTimeProvider _clock = new();
    private readonly InMemoryBudgetStore _store;

    public InMemoryBudgetStoreTests() => _store = new InMemoryBudgetStore(_clock);

    protected override IBudgetStore Store => _store;

    protected override Task WaitPastAsync(TimeSpan lifetime)
    {
        _clock.Advance(lifetime + TimeSpan.FromMilliseconds(1));
        return Task.CompletedTask;
    }
}

public sealed class RedisBudgetStoreTests(RedisFixture redis) : BudgetStoreContract, IClassFixture<RedisFixture>
{
    protected override IBudgetStore Store { get; } = new RedisBudgetStore(redis.Connection);

    protected override Task WaitPastAsync(TimeSpan lifetime) => Task.Delay(lifetime + TimeSpan.FromMilliseconds(100));
}
