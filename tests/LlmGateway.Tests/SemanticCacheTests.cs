using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Caching;
using LlmGateway.Providers;
using LlmGateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests;

public sealed class SemanticBucketTests
{
    private static JsonObject Chat(string system, string question, params string[] history)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var turn in history)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = turn });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
        return new JsonObject { ["model"] = "m", ["messages"] = messages };
    }

    [Fact]
    public void Only_the_last_question_may_differ_within_a_bucket()
    {
        var a = CacheKeys.SemanticBucket("t", Chat("Be brief.", "What is the capital of France?"))!.Value;
        var b = CacheKeys.SemanticBucket("t", Chat("Be brief.", "france capital?"))!.Value;
        var otherSystem = CacheKeys.SemanticBucket("t", Chat("Be verbose.", "What is the capital of France?"))!.Value;
        var otherHistory = CacheKeys.SemanticBucket("t", Chat("Be brief.", "What is the capital of France?", "Hi"))!.Value;
        var otherTeam = CacheKeys.SemanticBucket("u", Chat("Be brief.", "What is the capital of France?"))!.Value;

        Assert.Equal(a.Bucket, b.Bucket);
        Assert.Equal("What is the capital of France?", a.Question);
        Assert.NotEqual(a.Bucket, otherSystem.Bucket);
        Assert.NotEqual(a.Bucket, otherHistory.Bucket);
        Assert.NotEqual(a.Bucket, otherTeam.Bucket);
    }

    [Fact]
    public void Requests_not_ending_in_a_user_message_have_no_bucket()
    {
        var request = JsonNode.Parse("""{"model":"m","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]}""")!.AsObject();

        Assert.Null(CacheKeys.SemanticBucket("t", request));
    }

    [Fact]
    public async Task Mock_embeddings_ignore_case_and_punctuation_but_not_words()
    {
        var embeddings = new MockEmbeddingProvider();

        var a = await embeddings.EmbedAsync("What is the capital of France?", CancellationToken.None);
        var b = await embeddings.EmbedAsync("what is THE capital of france", CancellationToken.None);
        var c = await embeddings.EmbedAsync("Tell me a joke about cats", CancellationToken.None);

        Assert.Equal(1.0, Vectors.Cosine(a, b), precision: 5);
        Assert.True(Vectors.Cosine(a, c) < 0.3);
    }
}

public abstract class SemanticCacheContract
{
    protected abstract ISemanticCache Cache { get; }

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private static float[] Unit(params float[] v) => Vectors.Normalize(v);

    private static CachedCompletion Answer(string text) => new(text, "m", 1, 1, 1);

    [Fact]
    public async Task Returns_the_most_similar_entry_above_the_threshold()
    {
        var bucket = "scache:{t}:" + Guid.NewGuid().ToString("N");
        await Cache.AddAsync(bucket, Unit(1, 0, 0), Answer("x"), Ttl, 10);
        await Cache.AddAsync(bucket, Unit(1, 1, 0), Answer("xy"), Ttl, 10);

        var match = await Cache.FindAsync(bucket, Unit(1, 0.1f, 0), threshold: 0.9);

        Assert.Equal("x", match!.Completion.Body);
        Assert.True(match.Similarity > 0.99);
        Assert.Null(await Cache.FindAsync(bucket, Unit(0, 0, 1), threshold: 0.9));
        Assert.Null(await Cache.FindAsync("scache:{t}:empty", Unit(1, 0, 0), threshold: 0.9));
    }

    [Fact]
    public async Task Keeps_only_the_newest_entries()
    {
        var bucket = "scache:{t}:" + Guid.NewGuid().ToString("N");
        await Cache.AddAsync(bucket, Unit(1, 0, 0), Answer("oldest"), Ttl, 2);
        await Cache.AddAsync(bucket, Unit(0, 1, 0), Answer("middle"), Ttl, 2);
        await Cache.AddAsync(bucket, Unit(0, 0, 1), Answer("newest"), Ttl, 2);

        Assert.Null(await Cache.FindAsync(bucket, Unit(1, 0, 0), 0.9));
        Assert.Equal("newest", (await Cache.FindAsync(bucket, Unit(0, 0, 1), 0.9))!.Completion.Body);
    }
}

public sealed class InMemorySemanticCacheTests : SemanticCacheContract
{
    protected override ISemanticCache Cache { get; } = new InMemorySemanticCache();
}

public sealed class RedisSemanticCacheTests(RedisFixture redis) : SemanticCacheContract, IClassFixture<RedisFixture>
{
    protected override ISemanticCache Cache { get; } = new RedisSemanticCache(redis.Connection);
}

public sealed class SemanticCacheEndpointTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    private const string Completion =
        """{"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"Paris."},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":10}}""";

    private (WebApplicationFactory<Program> Factory, FakeUpstreamHandler Upstream) CreateGateway(params (string Key, string Value)[] extra)
    {
        var upstream = new FakeUpstreamHandler { Respond = _ => FakeUpstreamHandler.Json(HttpStatusCode.OK, Completion) };
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Cache:DefaultOn"] = "true",
            ["Gateway:Cache:SemanticEnabled"] = "true",
            ["Gateway:Cache:SimilarityThreshold"] = "0.9",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(OpenAiCompatibleProvider.HttpClientName("openai"))
                    .ConfigurePrimaryHttpMessageHandler(() => upstream));
        });
        return (factory, upstream);
    }

    private static object Ask(string question, string system = "Answer briefly.") => new
    {
        model = "gpt-4o-mini",
        messages = new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = question },
        },
    };

    [Fact]
    public async Task Rephrased_question_is_answered_from_the_cache()
    {
        var (factory, upstream) = CreateGateway();
        var client = factory.CreateAuthorizedClient();

        var first = await client.PostAsJsonAsync("/v1/chat/completions", Ask("What is the capital of France?"));
        var rephrased = await client.PostAsJsonAsync("/v1/chat/completions", Ask("what is the capital of france"));

        Assert.Equal("miss", first.Headers.GetValues("x-gateway-cache").Single());
        Assert.Equal("semantic-hit", rephrased.Headers.GetValues("x-gateway-cache").Single());
        Assert.Equal("1.000", rephrased.Headers.GetValues("x-gateway-cache-similarity").Single());
        Assert.Single(upstream.Requests);

        var usage = await factory.WaitForUsageAsync(rephrased.RequestId());
        Assert.Equal("semantic-cache", usage.Provider);
        Assert.True(usage.CacheHit);
        Assert.Equal(21, usage.SavedMicros); // 100 x $0.15 + 10 x $0.60 per 1M
    }

    [Fact]
    public async Task Different_question_or_different_system_prompt_is_a_miss()
    {
        var (factory, upstream) = CreateGateway();
        var client = factory.CreateAuthorizedClient();

        await client.PostAsJsonAsync("/v1/chat/completions", Ask("What is the capital of France?"));
        var otherQuestion = await client.PostAsJsonAsync("/v1/chat/completions", Ask("Tell me a joke about cats"));
        var otherSystem = await client.PostAsJsonAsync("/v1/chat/completions", Ask("What is the capital of France?", "Answer in French."));

        Assert.Equal("miss", otherQuestion.Headers.GetValues("x-gateway-cache").Single());
        Assert.Equal("miss", otherSystem.Headers.GetValues("x-gateway-cache").Single());
        Assert.Equal(3, upstream.Requests.Count);
    }

    [Fact]
    public async Task Real_embeddings_come_from_the_providers_embeddings_endpoint()
    {
        var (factory, upstream) = CreateGateway(("Gateway:Cache:Embeddings:Provider", "openai"));
        upstream.Respond = request => request.Uri!.AbsolutePath.EndsWith("/embeddings")
            ? FakeUpstreamHandler.Json(HttpStatusCode.OK, """{"data":[{"embedding":[3.0,4.0]}]}""")
            : FakeUpstreamHandler.Json(HttpStatusCode.OK, Completion);
        var client = factory.CreateAuthorizedClient();

        await client.PostAsJsonAsync("/v1/chat/completions", Ask("first wording"));
        var second = await client.PostAsJsonAsync("/v1/chat/completions", Ask("second wording"));

        // Both questions got the same fake vector, so the second is a semantic hit.
        Assert.Equal("semantic-hit", second.Headers.GetValues("x-gateway-cache").Single());
        var embeddingCall = upstream.Requests.First(r => r.Uri!.AbsolutePath.EndsWith("/embeddings"));
        Assert.Equal("text-embedding-3-small", JsonNode.Parse(embeddingCall.Body)!["model"]!.GetValue<string>());
        Assert.Equal("first wording", JsonNode.Parse(embeddingCall.Body)!["input"]!.GetValue<string>());
    }

    [Fact]
    public async Task Embedding_failures_fall_back_to_a_normal_request()
    {
        var (factory, upstream) = CreateGateway(("Gateway:Cache:Embeddings:Provider", "openai"));
        upstream.Respond = request => request.Uri!.AbsolutePath.EndsWith("/embeddings")
            ? FakeUpstreamHandler.Json(HttpStatusCode.InternalServerError, "{}")
            : FakeUpstreamHandler.Json(HttpStatusCode.OK, Completion);

        var response = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Ask("anything"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("miss", response.Headers.GetValues("x-gateway-cache").Single());
    }
}
