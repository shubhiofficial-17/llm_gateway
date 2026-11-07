using StackExchange.Redis;

namespace LlmGateway.Limits;

public sealed class BudgetOptions
{
    /// <summary>Monthly spend allowed per team, in USD. 0 means no budget unless a team has its own.</summary>
    public decimal DefaultMonthlyBudgetUsd { get; set; }

    /// <summary>How long a reservation is held if its request never settles (e.g. the gateway crashed).</summary>
    public int ReservationTtlSeconds { get; set; } = 600;
}

public enum BudgetStatus
{
    Reserved,
    OverBudget,
    NeedsSeed,
}

public sealed record BudgetDecision(BudgetStatus Status, long RemainingMicros);

/// <summary>Per-team, per-month spend counter: "used" (settled) plus "reserved" (in flight).</summary>
public interface IBudgetStore
{
    Task<BudgetDecision> TryReserveAsync(string teamId, string month, long limitMicros, long estimateMicros,
        string reservationId, TimeSpan reservationTtl);

    Task SettleAsync(string teamId, string month, string reservationId, long reservedMicros, long actualMicros);

    /// <summary>Sets "used" if it isn't set yet. Called with the ledger's total after Redis lost its data.</summary>
    Task SeedAsync(string teamId, string month, long usedMicros);
}

public sealed class RedisBudgetStore(IConnectionMultiplexer redis) : IBudgetStore
{
    private static readonly string ReserveScript = EmbeddedScript.Read("budget_reserve.lua");
    private static readonly string SettleScript = EmbeddedScript.Read("budget_settle.lua");

    // Comfortably longer than a month, so a key never expires while its month is current.
    private const int KeyTtlSeconds = 40 * 24 * 3600;

    private static RedisKey[] Keys(string teamId, string month) =>
        [$"budget:{{{teamId}}}:{month}", $"budget:{{{teamId}}}:{month}:reservations"];

    public async Task<BudgetDecision> TryReserveAsync(string teamId, string month, long limitMicros, long estimateMicros,
        string reservationId, TimeSpan reservationTtl)
    {
        var result = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(ReserveScript, Keys(teamId, month),
            [limitMicros, estimateMicros, reservationId, (long)reservationTtl.TotalMilliseconds, KeyTtlSeconds]))!;

        var status = (int)result[0] switch
        {
            1 => BudgetStatus.Reserved,
            0 => BudgetStatus.OverBudget,
            _ => BudgetStatus.NeedsSeed,
        };
        return new BudgetDecision(status, (long)result[1]);
    }

    public Task SettleAsync(string teamId, string month, string reservationId, long reservedMicros, long actualMicros) =>
        redis.GetDatabase().ScriptEvaluateAsync(SettleScript, Keys(teamId, month),
            [$"{reservationId}:{reservedMicros}", reservedMicros, actualMicros]);

    public async Task SeedAsync(string teamId, string month, long usedMicros)
    {
        var db = redis.GetDatabase();
        var key = Keys(teamId, month)[0];
        await db.HashSetAsync(key, "used", usedMicros, When.NotExists);
        await db.KeyExpireAsync(key, TimeSpan.FromSeconds(KeyTtlSeconds));
    }
}

/// <summary>Single-instance version used when Redis isn't configured (tests, quick local runs).</summary>
public sealed class InMemoryBudgetStore(TimeProvider time) : IBudgetStore
{
    private readonly Dictionary<string, Account> _accounts = new();

    public Task<BudgetDecision> TryReserveAsync(string teamId, string month, long limitMicros, long estimateMicros,
        string reservationId, TimeSpan reservationTtl)
    {
        lock (_accounts)
        {
            if (!_accounts.TryGetValue(Key(teamId, month), out var account))
            {
                return Task.FromResult(new BudgetDecision(BudgetStatus.NeedsSeed, 0));
            }

            var now = time.GetUtcNow();
            foreach (var (id, reservation) in account.Reservations.Where(r => r.Value.ExpiresAt <= now).ToList())
            {
                account.Reserved -= reservation.Amount;
                account.Reservations.Remove(id);
            }

            if (account.Used + account.Reserved + estimateMicros > limitMicros)
            {
                return Task.FromResult(new BudgetDecision(BudgetStatus.OverBudget, limitMicros - account.Used - account.Reserved));
            }

            account.Reserved += estimateMicros;
            account.Reservations[reservationId] = (estimateMicros, now + reservationTtl);
            return Task.FromResult(new BudgetDecision(BudgetStatus.Reserved, limitMicros - account.Used - account.Reserved));
        }
    }

    public Task SettleAsync(string teamId, string month, string reservationId, long reservedMicros, long actualMicros)
    {
        lock (_accounts)
        {
            if (_accounts.TryGetValue(Key(teamId, month), out var account))
            {
                if (account.Reservations.Remove(reservationId))
                {
                    account.Reserved -= reservedMicros;
                }

                account.Used += actualMicros;
            }
        }

        return Task.CompletedTask;
    }

    public Task SeedAsync(string teamId, string month, long usedMicros)
    {
        lock (_accounts)
        {
            _accounts.TryAdd(Key(teamId, month), new Account { Used = usedMicros });
        }

        return Task.CompletedTask;
    }

    private static string Key(string teamId, string month) => $"{teamId}:{month}";

    private sealed class Account
    {
        public long Used { get; set; }
        public long Reserved { get; set; }
        public Dictionary<string, (long Amount, DateTimeOffset ExpiresAt)> Reservations { get; } = new();
    }
}
