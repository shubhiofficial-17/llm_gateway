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
    DateTimeOffset CreatedAt);

public sealed record ModelUsage(
    string Model,
    long Requests,
    long PromptTokens,
    long CompletionTokens,
    long CostMicros);

public sealed record UsageSummary(
    string TeamId,
    DateTimeOffset From,
    DateTimeOffset To,
    long Requests,
    long CostMicros,
    decimal CostUsd,
    IReadOnlyList<ModelUsage> ByModel);
