namespace LlmGateway.Usage;

public sealed record UsageEvent(
    string RequestId,
    string TeamId,
    string Model,
    string Provider,
    int PromptTokens,
    int CompletionTokens,
    long CostMicros,
    int StatusCode,
    int LatencyMs,
    bool Streamed,
    bool UsageEstimated,
    DateTimeOffset CreatedAt,
    bool CacheHit = false,
    long SavedMicros = 0);

public sealed record ModelUsage(
    string Model,
    long Requests,
    long PromptTokens,
    long CompletionTokens,
    long CostMicros,
    long CacheHits = 0,
    long SavedMicros = 0);

public sealed record UsageSummary(
    string TeamId,
    DateTimeOffset From,
    DateTimeOffset To,
    long Requests,
    long CostMicros,
    decimal CostUsd,
    long CacheHits,
    decimal SavedUsd,
    IReadOnlyList<ModelUsage> ByModel);
