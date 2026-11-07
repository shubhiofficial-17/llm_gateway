using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using LlmGateway.Config;

namespace LlmGateway.Providers.Anthropic;

/// <summary>Calls Anthropic's Messages API and presents the result in OpenAI's format.</summary>
public sealed class AnthropicProvider(
    string name,
    ProviderOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<AnthropicProvider> logger) : IChatProvider
{
    public const string ApiVersion = "2023-06-01";

    public string Name => name;

    public async Task<ProviderResponse> SendChatAsync(ProviderRequest request, CancellationToken ct)
    {
        var body = AnthropicTranslator.ToAnthropicRequest(request.Body, request.UpstreamModel, options.DefaultMaxTokens);

        var client = httpClientFactory.CreateClient(OpenAiCompatibleProvider.HttpClientName(name));
        using var message = new HttpRequestMessage(HttpMethod.Post, "messages");
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            message.Headers.TryAddWithoutValidation("x-api-key", options.ApiKey);
        }

        var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                return Json(AnthropicTranslator.ToOpenAiStatus(status), AnthropicTranslator.ToOpenAiError(errorBody, status));
            }
        }

        if (!request.Stream)
        {
            using (response)
            {
                var completion = await response.Content.ReadFromJsonAsync<JsonObject>(ct)
                                 ?? throw new HttpRequestException("Anthropic returned an empty response.");
                return Json(status, AnthropicTranslator.ToOpenAiCompletion(completion, request.UpstreamModel));
            }
        }

        // Translate the event stream on the fly; the gateway relays the translated stream as usual.
        var upstream = await response.Content.ReadAsStreamAsync(ct);
        var includeUsage = request.Body["stream_options"] is JsonObject so
                           && so["include_usage"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var pipe = new Pipe();
        _ = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await AnthropicTranslator.TranslateStreamAsync(upstream, pipe.Writer, request.UpstreamModel, includeUsage, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Anthropic stream for {RequestId} broke", request.RequestId);
                failure = new IOException("Anthropic stream broke.", ex); // surfaces to the relay as a broken stream
            }
            finally
            {
                await pipe.Writer.CompleteAsync(failure);
                response.Dispose();
            }
        }, CancellationToken.None);

        return new ProviderResponse
        {
            StatusCode = status,
            ContentType = "text/event-stream",
            Body = pipe.Reader.AsStream(),
        };
    }

    private static ProviderResponse Json(int status, JsonObject body) => new()
    {
        StatusCode = status,
        ContentType = "application/json",
        Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString())),
    };
}
