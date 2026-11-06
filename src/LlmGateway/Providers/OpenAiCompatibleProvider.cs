using System.Net.Http.Headers;
using System.Text;
using LlmGateway.Config;

namespace LlmGateway.Providers;

/// <summary>Any upstream that speaks OpenAI's /chat/completions API: OpenAI, Ollama, Groq, vLLM, LM Studio...</summary>
public sealed class OpenAiCompatibleProvider(string name, ProviderOptions options, IHttpClientFactory httpClientFactory)
    : IChatProvider
{
    public string Name => name;

    public static string HttpClientName(string providerName) => $"provider:{providerName}";

    public async Task<ProviderResponse> SendChatAsync(ProviderRequest request, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName(name));

        using var message = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        message.Content = new StringContent(request.Body.ToJsonString(), Encoding.UTF8, "application/json");
        message.Headers.TryAddWithoutValidation("x-request-id", request.RequestId);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        // ResponseHeadersRead: return as soon as headers arrive instead of buffering the whole body.
        var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStreamAsync(ct);

        return new ProviderResponse
        {
            StatusCode = (int)response.StatusCode,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            Body = body,
            Owner = response,
        };
    }
}
