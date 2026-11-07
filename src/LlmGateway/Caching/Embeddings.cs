using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlmGateway.Config;
using LlmGateway.Providers;
using Microsoft.Extensions.Options;

namespace LlmGateway.Caching;

public interface IEmbeddingProvider
{
    /// <summary>Unit-length embedding of <paramref name="text"/>.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}

public static class Vectors
{
    /// <summary>Cosine similarity of two unit vectors is just their dot product.</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    public static float[] Normalize(float[] v)
    {
        double norm = 0;
        foreach (var x in v)
        {
            norm += x * x;
        }

        norm = Math.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] = (float)(v[i] / norm);
            }
        }

        return v;
    }
}

/// <summary>
/// Deterministic bag-of-words embedding (feature hashing) for tests and keyless demos. Texts with the
/// same words score 1.0 regardless of case and punctuation; it has no real notion of meaning.
/// </summary>
public sealed partial class MockEmbeddingProvider : IEmbeddingProvider
{
    private const int Dimensions = 256;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var vector = new float[Dimensions];
        foreach (Match word in Words().Matches(text.ToLowerInvariant()))
        {
            // Stable hash (string.GetHashCode is randomised per process).
            var hash = 2166136261u;
            foreach (var c in word.Value)
            {
                hash = (hash ^ c) * 16777619u;
            }

            vector[hash % Dimensions] += 1;
        }

        return Task.FromResult(Vectors.Normalize(vector));
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}

/// <summary>Calls /embeddings on an OpenAI-compatible provider (OpenAI, Ollama, vLLM...).</summary>
public sealed class OpenAiEmbeddingProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<GatewayOptions> gateway,
    IOptionsMonitor<CacheOptions> cache) : IEmbeddingProvider
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var settings = cache.CurrentValue.Embeddings;
        var provider = gateway.Value.Providers[settings.Provider];
        var client = httpClientFactory.CreateClient(OpenAiCompatibleProvider.HttpClientName(settings.Provider));

        using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings");
        request.Content = new StringContent(
            new JsonObject { ["model"] = settings.Model, ["input"] = text }.ToJsonString(), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var values = body?["data"]?[0]?["embedding"]?.AsArray()
                     ?? throw new InvalidOperationException("Embedding response had no data[0].embedding.");

        return Vectors.Normalize(values.Select(v => v!.GetValue<float>()).ToArray());
    }
}
