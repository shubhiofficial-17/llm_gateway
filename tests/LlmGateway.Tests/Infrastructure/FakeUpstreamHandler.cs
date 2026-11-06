using System.Net;
using System.Text;

namespace LlmGateway.Tests.Infrastructure;

public sealed record CapturedRequest(Uri? Uri, string? Authorization, string Body);

/// <summary>Stands in for an OpenAI-compatible provider over HTTP.</summary>
public sealed class FakeUpstreamHandler : HttpMessageHandler
{
    private readonly List<CapturedRequest> _requests = new();

    public Func<CapturedRequest, HttpResponseMessage> Respond { get; set; } =
        _ => Json(HttpStatusCode.OK, "{}");

    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new CapturedRequest(request.RequestUri, request.Headers.Authorization?.ToString(), body);
        lock (_requests)
        {
            _requests.Add(captured);
        }

        return Respond(captured);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage EventStream(string sse) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
    };
}
