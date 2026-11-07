using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace LlmGateway.Providers;

public sealed class CircuitBreakerOptions
{
    /// <summary>Consecutive failures that open a provider's circuit. 0 turns circuit breaking off.</summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>How long an open circuit stays open before one trial request is let through.</summary>
    public int BreakSeconds { get; set; } = 30;
}

[JsonConverter(typeof(JsonStringEnumConverter<CircuitState>))]
public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}

public sealed record ProviderHealth(string Provider, CircuitState State, int ConsecutiveFailures, DateTimeOffset? OpenUntil);

/// <summary>
/// One circuit breaker per provider, per gateway instance.
/// Closed: requests flow. After N consecutive failures it opens: requests skip the provider (and go to
/// a fallback, or fail fast) for the break duration. Then it's half-open: one trial request goes
/// through; success closes the circuit, failure opens it again. See ADR 0010.
/// </summary>
public sealed class CircuitBreakers(
    IOptionsMonitor<CircuitBreakerOptions> options,
    TimeProvider time,
    ILogger<CircuitBreakers> logger)
{
    private readonly ConcurrentDictionary<string, Circuit> _circuits = new(StringComparer.OrdinalIgnoreCase);

    private TimeSpan BreakDuration => TimeSpan.FromSeconds(options.CurrentValue.BreakSeconds);

    /// <summary>
    /// Whether a request may be sent to the provider now. When it may not, <paramref name="retryAfter"/>
    /// says roughly when to try again.
    /// </summary>
    public bool TryEnter(string provider, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (options.CurrentValue.FailureThreshold <= 0)
        {
            return true;
        }

        var circuit = _circuits.GetOrAdd(provider, _ => new Circuit());
        var now = time.GetUtcNow();
        lock (circuit)
        {
            switch (circuit.State)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open when now >= circuit.OpenUntil:
                    circuit.State = CircuitState.HalfOpen;
                    circuit.ProbeStartedAt = now;
                    logger.LogInformation("Circuit for {Provider} is half-open; sending a trial request", provider);
                    return true;

                case CircuitState.Open:
                    retryAfter = circuit.OpenUntil - now;
                    return false;

                // Half-open: only one trial at a time. If a trial never reported back, allow another.
                case CircuitState.HalfOpen when circuit.ProbeStartedAt is null || now - circuit.ProbeStartedAt >= BreakDuration:
                    circuit.ProbeStartedAt = now;
                    return true;

                default:
                    retryAfter = TimeSpan.FromSeconds(1);
                    return false;
            }
        }
    }

    /// <summary>The provider answered (even with a client error or a rate limit): it is up.</summary>
    public void RecordSuccess(string provider)
    {
        if (!_circuits.TryGetValue(provider, out var circuit))
        {
            return;
        }

        lock (circuit)
        {
            if (circuit.State != CircuitState.Closed)
            {
                logger.LogInformation("Circuit for {Provider} closed; provider is healthy again", provider);
            }

            circuit.State = CircuitState.Closed;
            circuit.ConsecutiveFailures = 0;
            circuit.ProbeStartedAt = null;
        }
    }

    /// <summary>Connection error, timeout or 5xx.</summary>
    public void RecordFailure(string provider)
    {
        var threshold = options.CurrentValue.FailureThreshold;
        if (threshold <= 0)
        {
            return;
        }

        var circuit = _circuits.GetOrAdd(provider, _ => new Circuit());
        lock (circuit)
        {
            circuit.ConsecutiveFailures++;
            if (circuit.State == CircuitState.HalfOpen || circuit.ConsecutiveFailures >= threshold)
            {
                if (circuit.State != CircuitState.Open)
                {
                    logger.LogWarning("Circuit for {Provider} opened after {Failures} consecutive failures; skipping it for {Seconds}s",
                        provider, circuit.ConsecutiveFailures, options.CurrentValue.BreakSeconds);
                }

                circuit.State = CircuitState.Open;
                circuit.OpenUntil = time.GetUtcNow() + BreakDuration;
                circuit.ProbeStartedAt = null;
            }
        }
    }

    /// <summary>The request was cancelled by the client before the provider answered: no verdict either way.</summary>
    public void Abandon(string provider)
    {
        if (_circuits.TryGetValue(provider, out var circuit))
        {
            lock (circuit)
            {
                circuit.ProbeStartedAt = null;
            }
        }
    }

    public ProviderHealth Health(string provider)
    {
        if (!_circuits.TryGetValue(provider, out var circuit))
        {
            return new ProviderHealth(provider, CircuitState.Closed, 0, null);
        }

        lock (circuit)
        {
            return new ProviderHealth(provider, circuit.State, circuit.ConsecutiveFailures,
                circuit.State == CircuitState.Open ? circuit.OpenUntil : null);
        }
    }

    private sealed class Circuit
    {
        public CircuitState State { get; set; } = CircuitState.Closed;
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset OpenUntil { get; set; }
        public DateTimeOffset? ProbeStartedAt { get; set; }
    }
}
