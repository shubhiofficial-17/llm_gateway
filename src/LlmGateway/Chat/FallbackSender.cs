using System.Text.Json.Nodes;
using LlmGateway.Providers;

namespace LlmGateway.Chat;

/// <summary>
/// Outcome of sending a request down a model's fallback chain. Either a provider responded
/// (<see cref="Response"/> is set, possibly with an error status that should be passed through),
/// or every provider failed before responding (<see cref="Response"/> is null).
/// </summary>
public sealed record SendResult(
    ModelRoute Served,
    ProviderResponse? Response,
    int Attempts,
    int FailureStatus = 0,
    string? FailureCode = null,
    string? FailureMessage = null);

/// <summary>
/// Tries the requested model, then each configured fallback, until one gives a response worth
/// returning. Only failures before any response bytes reach the client can fall back: once a
/// stream has started, it can't be restarted on another model. See ADR 0009.
/// </summary>
public sealed class FallbackSender(ILogger<FallbackSender> logger)
{
    /// <summary>Statuses that mean "this provider can't serve it right now", so another may.</summary>
    public static bool IsRetryable(int status) => status is 408 or 429 or >= 500;

    public async Task<SendResult> SendAsync(
        ModelRoute route, JsonObject upstreamBody, bool stream, string requestId, CancellationToken ct)
    {
        var chain = route.Chain.ToList();
        SendResult? failure = null;

        for (var i = 0; i < chain.Count; i++)
        {
            var candidate = chain[i];
            var isLast = i == chain.Count - 1;

            var body = (JsonObject)upstreamBody.DeepClone();
            body["model"] = candidate.UpstreamModel;

            try
            {
                var response = await candidate.Provider.SendChatAsync(
                    new ProviderRequest(requestId, candidate.UpstreamModel, body, stream), ct);

                if (response.IsSuccess || !IsRetryable(response.StatusCode) || isLast)
                {
                    return new SendResult(candidate, response, i + 1);
                }

                logger.LogWarning("{Model} via {Provider} returned {Status} for {RequestId}; trying fallback",
                    candidate.Model, candidate.Provider.Name, response.StatusCode, requestId);
                await response.DisposeAsync();
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "{Model} via {Provider} unreachable for {RequestId}",
                    candidate.Model, candidate.Provider.Name, requestId);
                failure = new SendResult(candidate, null, i + 1, StatusCodes.Status502BadGateway, "upstream_unreachable",
                    $"Upstream provider '{candidate.Provider.Name}' is unreachable.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("{Model} via {Provider} timed out for {RequestId}",
                    candidate.Model, candidate.Provider.Name, requestId);
                failure = new SendResult(candidate, null, i + 1, StatusCodes.Status504GatewayTimeout, "upstream_timeout",
                    $"Upstream provider '{candidate.Provider.Name}' timed out.");
            }
        }

        // Only reachable when the last candidate threw.
        return failure!;
    }
}
