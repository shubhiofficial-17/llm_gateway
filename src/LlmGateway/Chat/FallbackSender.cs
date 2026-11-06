using System.Text.Json.Nodes;
using LlmGateway.Providers;

namespace LlmGateway.Chat;

/// <summary>
/// Outcome of sending a request down a model's fallback chain. Either a provider responded
/// (<see cref="Response"/> is set, possibly with an error status that should be passed through),
/// or no provider gave a response (<see cref="Response"/> is null).
/// </summary>
public sealed record SendResult(
    ModelRoute Served,
    ProviderResponse? Response,
    int Attempts,
    int FailureStatus = 0,
    string? FailureCode = null,
    string? FailureMessage = null,
    TimeSpan? RetryAfter = null);

/// <summary>
/// Tries the requested model, then each configured fallback, until one gives a response worth
/// returning. Providers whose circuit is open are skipped without being called. Only failures
/// before any response bytes reach the client can fall back: once a stream has started, it
/// can't be restarted on another model. See ADRs 0009 and 0010.
/// </summary>
public sealed class FallbackSender(CircuitBreakers breakers, ILogger<FallbackSender> logger)
{
    /// <summary>Statuses that mean "this provider can't serve it right now", so another may.</summary>
    public static bool IsRetryable(int status) => status is 408 or 429 or >= 500;

    /// <summary>Statuses that count against a provider's health. A 429 means it's up but busy.</summary>
    public static bool IsProviderFailure(int status) => status is 408 or >= 500;

    public async Task<SendResult> SendAsync(
        ModelRoute route, JsonObject upstreamBody, bool stream, string requestId, CancellationToken ct)
    {
        var attempts = 0;
        TimeSpan? soonestRetry = null;

        // The most recent failure, kept so it can be returned if nothing after it succeeds.
        SendResult? lastFailure = null;

        foreach (var candidate in route.Chain)
        {
            var provider = candidate.Provider.Name;
            if (!breakers.TryEnter(provider, out var retryAfter))
            {
                logger.LogDebug("Skipping {Model}: circuit for {Provider} is open", candidate.Model, provider);
                soonestRetry = soonestRetry is null || retryAfter < soonestRetry ? retryAfter : soonestRetry;
                continue;
            }

            attempts++;
            var body = (JsonObject)upstreamBody.DeepClone();
            body["model"] = candidate.UpstreamModel;

            try
            {
                var response = await candidate.Provider.SendChatAsync(
                    new ProviderRequest(requestId, candidate.UpstreamModel, body, stream), ct);

                if (IsProviderFailure(response.StatusCode))
                {
                    breakers.RecordFailure(provider);
                }
                else
                {
                    breakers.RecordSuccess(provider);
                }

                if (response.IsSuccess || !IsRetryable(response.StatusCode))
                {
                    await DisposeAsync(lastFailure);
                    return new SendResult(candidate, response, attempts);
                }

                logger.LogWarning("{Model} via {Provider} returned {Status} for {RequestId}; trying fallback",
                    candidate.Model, provider, response.StatusCode, requestId);
                await DisposeAsync(lastFailure);
                lastFailure = new SendResult(candidate, response, attempts);
            }
            catch (HttpRequestException ex)
            {
                breakers.RecordFailure(provider);
                logger.LogWarning(ex, "{Model} via {Provider} unreachable for {RequestId}", candidate.Model, provider, requestId);
                await DisposeAsync(lastFailure);
                lastFailure = new SendResult(candidate, null, attempts, StatusCodes.Status502BadGateway,
                    "upstream_unreachable", $"Upstream provider '{provider}' is unreachable.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                breakers.RecordFailure(provider);
                logger.LogWarning("{Model} via {Provider} timed out for {RequestId}", candidate.Model, provider, requestId);
                await DisposeAsync(lastFailure);
                lastFailure = new SendResult(candidate, null, attempts, StatusCodes.Status504GatewayTimeout,
                    "upstream_timeout", $"Upstream provider '{provider}' timed out.");
            }
            catch (OperationCanceledException)
            {
                breakers.Abandon(provider); // the client left; says nothing about the provider
                await DisposeAsync(lastFailure);
                throw;
            }
        }

        if (lastFailure is not null)
        {
            return lastFailure with { Attempts = attempts };
        }

        // Every provider in the chain has an open circuit: fail fast instead of waiting on known-bad providers.
        return new SendResult(route, null, 0, StatusCodes.Status503ServiceUnavailable, "provider_unavailable",
            $"Every provider for '{route.Model}' is failing right now, so the request was not sent. " +
            "Try again shortly.", soonestRetry);
    }

    private static async Task DisposeAsync(SendResult? result)
    {
        if (result?.Response is { } response)
        {
            await response.DisposeAsync();
        }
    }
}
