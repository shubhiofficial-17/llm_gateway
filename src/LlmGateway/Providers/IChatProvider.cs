using System.Text.Json.Nodes;

namespace LlmGateway.Providers;

public interface IChatProvider
{
    string Name { get; }

    /// <summary>
    /// Sends an OpenAI-format chat completion request. The returned body is not buffered, so a
    /// streaming response can be relayed to the client as it arrives.
    /// </summary>
    Task<ProviderResponse> SendChatAsync(ProviderRequest request, CancellationToken ct);
}

public sealed record ProviderRequest(string RequestId, string UpstreamModel, JsonObject Body, bool Stream);

public sealed class ProviderResponse : IAsyncDisposable
{
    public required int StatusCode { get; init; }

    public string? ContentType { get; init; }

    public required Stream Body { get; init; }

    /// <summary>Anything that must be disposed with the body, e.g. the HttpResponseMessage.</summary>
    public IDisposable? Owner { get; init; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync();
        Owner?.Dispose();
    }
}
