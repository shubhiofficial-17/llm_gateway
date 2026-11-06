using System.Diagnostics;
using System.Diagnostics.Metrics;
using LlmGateway.Providers;
using LlmGateway.Usage;

namespace LlmGateway.Telemetry;

/// <summary>
/// The gateway's own traces and metrics. Span and attribute names follow the OpenTelemetry
/// GenAI semantic conventions (gen_ai.*) where one exists. See ADR 0013.
/// </summary>
public sealed class GatewayTelemetry : IDisposable
{
    public const string Name = "LlmGateway";

    public static readonly ActivitySource Source = new(Name);

    private readonly Meter _meter;
    private readonly Counter<long> _requests;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _costMicros;
    private readonly Counter<long> _savedMicros;
    private readonly Histogram<double> _duration;
    private readonly Histogram<double> _timeToFirstToken;
    private readonly Counter<long> _fallbacks;
    private readonly Counter<long> _limitRejections;

    public GatewayTelemetry(IMeterFactory meterFactory, CircuitBreakers breakers, ProviderRegistry registry)
    {
        _meter = meterFactory.Create(Name);
        _requests = _meter.CreateCounter<long>("gateway.requests", description: "Chat completion requests, by outcome");
        _tokens = _meter.CreateCounter<long>("gateway.tokens", description: "Tokens billed, by type (input/output)");
        _costMicros = _meter.CreateCounter<long>("gateway.cost.micros", description: "Spend in micro-USD");
        _savedMicros = _meter.CreateCounter<long>("gateway.cache.saved.micros", description: "Spend avoided by cache hits, in micro-USD");
        _duration = _meter.CreateHistogram<double>("gateway.request.duration", unit: "ms", description: "Time to complete a chat request");
        _timeToFirstToken = _meter.CreateHistogram<double>("gateway.time_to_first_token", unit: "ms",
            description: "Time from request to the first streamed chunk");
        _fallbacks = _meter.CreateCounter<long>("gateway.fallbacks", description: "Requests answered by a fallback model");
        _limitRejections = _meter.CreateCounter<long>("gateway.limit.rejections", description: "Requests refused by a limit or budget");

        // 0 = closed, 1 = half-open, 2 = open.
        _meter.CreateObservableGauge("gateway.circuit.state", () => registry.ProviderNames
            .Select(p => new Measurement<int>(
                breakers.Health(p).State switch
                {
                    CircuitState.Open => 2,
                    CircuitState.HalfOpen => 1,
                    _ => 0,
                },
                new KeyValuePair<string, object?>("provider", p))),
            description: "Circuit breaker state per provider: 0 closed, 1 half-open, 2 open");
    }

    /// <summary>Called once per finished request, with the same data that goes to the ledger.</summary>
    public void RequestCompleted(UsageEvent e)
    {
        var tags = new TagList
        {
            { "team", e.TeamId },
            { "model", e.Model },
            { "provider", e.Provider },
            { "status", e.StatusCode },
            { "cache", e.CacheHit ? "hit" : "none" },
        };
        _requests.Add(1, tags);

        var costTags = new TagList { { "team", e.TeamId }, { "model", e.Model } };
        if (e.CacheHit)
        {
            _savedMicros.Add(e.SavedMicros, costTags);
            return;
        }

        _tokens.Add(e.PromptTokens, new TagList { { "team", e.TeamId }, { "model", e.Model }, { "type", "input" } });
        _tokens.Add(e.CompletionTokens, new TagList { { "team", e.TeamId }, { "model", e.Model }, { "type", "output" } });
        _costMicros.Add(e.CostMicros, costTags);
        _duration.Record(e.LatencyMs, new TagList { { "model", e.Model }, { "provider", e.Provider }, { "streamed", e.Streamed } });
    }

    public void FirstToken(string model, string provider, TimeSpan elapsed) =>
        _timeToFirstToken.Record(elapsed.TotalMilliseconds, new TagList { { "model", model }, { "provider", provider } });

    public void FallbackUsed(string requestedModel, string servedModel) =>
        _fallbacks.Add(1, new TagList { { "requested_model", requestedModel }, { "served_model", servedModel } });

    public void LimitRejected(string teamId, string limit) =>
        _limitRejections.Add(1, new TagList { { "team", teamId }, { "limit", limit } });

    public void Dispose() => _meter.Dispose();
}

/// <summary>Feeds every finished request to the metrics as well as the ledger.</summary>
public sealed class InstrumentedUsageRecorder(ChannelUsageRecorder inner, GatewayTelemetry telemetry) : IUsageRecorder
{
    public void Record(UsageEvent usageEvent)
    {
        inner.Record(usageEvent);
        telemetry.RequestCompleted(usageEvent);
    }
}
