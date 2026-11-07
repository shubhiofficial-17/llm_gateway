using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Auth;
using LlmGateway.Caching;
using LlmGateway.Providers;
using LlmGateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests;

public sealed class CacheKeyTests
{
    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Field_order_and_delivery_options_do_not_change_the_key()
    {
        var a = Parse("""{"model":"m","temperature":0,"messages":[{"role":"user","content":"hi"}]}""");
        var b = Parse("""{"messages":[{"content":"hi","role":"user"}],"temperature":0,"model":"m","user":"u1","stream_options":{"include_usage":true}}""");

        Assert.Equal(CacheKeys.For("t", a), CacheKeys.For("t", b));
    }

    [Fact]
    public void Anything_that_changes_the_answer_changes_the_key()
    {
        var baseline = Parse("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""");
        var otherPrompt = Parse("""{"model":"m","messages":[{"role":"user","content":"hello"}]}""");
        var otherModel = Parse("""{"model":"n","messages":[{"role":"user","content":"hi"}]}""");
        var otherTemperature = Parse("""{"model":"m","temperature":1,"messages":[{"role":"user","content":"hi"}]}""");

        var key = CacheKeys.For("t", baseline);
        Assert.NotEqual(key, CacheKeys.For("t", otherPrompt));
        Assert.NotEqual(key, CacheKeys.For("t", otherModel));
        Assert.NotEqual(key, CacheKeys.For("t", otherTemperature));
    }

    [Fact]
    public void Teams_never_share_keys()
    {
        var request = Parse("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""");

        Assert.NotEqual(CacheKeys.For("team-a", request), CacheKeys.For("team-b", request));
        Assert.StartsWith("cache:{team-a}:", CacheKeys.For("team-a", request));
    }
}

public sealed class ResponseCacheEndpointTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    private const string Completion =
        """{"id":"chatcmpl-1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"42"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1000,"completion_tokens":1000}}""";

    private static readonly object Request = new
    {
        model = "gpt-4o-mini",
        temperature = 0,
        messages = new[] { new { role = "user", content = "What is the answer?" } },
    };

    private (WebApplicationFactory<Program> Factory, FakeUpstreamHandler Upstream) CreateGateway()
    {
        var upstream = new FakeUpstreamHandler { Respond = _ => FakeUpstreamHandler.Json(HttpStatusCode.OK, Completion) };
        var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient(OpenAiCompatibleProvider.HttpClientName("openai"))
                .ConfigurePrimaryHttpMessageHandler(() => upstream)));
        return (factory, upstream);
    }

    private static HttpClient WithCacheHeader(HttpClient client, string value)
    {
        client.DefaultRequestHeaders.Add("x-gateway-cache", value);
        return client;
    }

    [Fact]
    public async Task Second_identical_request_is_served_from_cache_for_free()
    {
        var (factory, upstream) = CreateGateway();
        var client = WithCacheHeader(factory.CreateAuthorizedClient(), "on");

        var first = await client.PostAsJsonAsync("/v1/chat/completions", Request);
        var second = await client.PostAsJsonAsync("/v1/chat/completions", Request);

        Assert.Equal("miss", first.Headers.GetValues("x-gateway-cache").Single());
        Assert.Equal("hit", second.Headers.GetValues("x-gateway-cache").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Single(upstream.Requests);

        var firstUsage = await factory.WaitForUsageAsync(first.RequestId());
        var hitUsage = await factory.WaitForUsageAsync(second.RequestId());
        Assert.Equal(750, firstUsage.CostMicros); // 1000 x $0.15 + 1000 x $0.60 per 1M
        Assert.True(hitUsage.CacheHit);
        Assert.Equal("cache", hitUsage.Provider);
        Assert.Equal(0, hitUsage.CostMicros);
        Assert.Equal(750, hitUsage.SavedMicros);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", GatewayFactory.AdminKey);
        var summary = await admin.GetFromJsonAsync<JsonObject>($"/admin/teams/{GatewayFactory.TeamId}/usage");
        Assert.Equal(1, summary!["cacheHits"]!.GetValue<long>());
        Assert.Equal(0.00075m, summary["savedUsd"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Caching_is_opt_in()
    {
        var (factory, upstream) = CreateGateway();
        var client = factory.CreateAuthorizedClient();

        var first = await client.PostAsJsonAsync("/v1/chat/completions", Request);
        await client.PostAsJsonAsync("/v1/chat/completions", Request);

        Assert.Equal(2, upstream.Requests.Count);
        Assert.False(first.Headers.Contains("x-gateway-cache"));
    }

    [Fact]
    public async Task One_team_never_gets_another_teams_cached_answer()
    {
        var (factory, upstream) = CreateGateway();
        var teams = factory.Services.GetRequiredService<ITeamStore>();
        await teams.UpsertTeamAsync(new Team("team-b", "B"), CancellationToken.None);
        await teams.AddKeyAsync("team-b", ApiKeys.Hash("gw_team_b"), null, CancellationToken.None);

        await WithCacheHeader(factory.CreateAuthorizedClient(), "on").PostAsJsonAsync("/v1/chat/completions", Request);
        var other = await WithCacheHeader(factory.CreateAuthorizedClient("gw_team_b"), "on").PostAsJsonAsync("/v1/chat/completions", Request);

        Assert.Equal("miss", other.Headers.GetValues("x-gateway-cache").Single());
        Assert.Equal(2, upstream.Requests.Count);
    }

    [Fact]
    public async Task Streaming_requests_and_errors_are_not_cached()
    {
        var (factory, upstream) = CreateGateway();
        var client = WithCacheHeader(factory.CreateAuthorizedClient(), "on");

        upstream.Respond = _ => FakeUpstreamHandler.Json(HttpStatusCode.InternalServerError, """{"error":{"message":"boom"}}""");
        await client.PostAsJsonAsync("/v1/chat/completions", Request);

        upstream.Respond = _ => FakeUpstreamHandler.Json(HttpStatusCode.OK, Completion);
        var afterError = await client.PostAsJsonAsync("/v1/chat/completions", Request);
        Assert.Equal("miss", afterError.Headers.GetValues("x-gateway-cache").Single());

        upstream.Respond = _ => FakeUpstreamHandler.EventStream("data: [DONE]\n\n");
        var streamed = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "gpt-4o-mini",
            temperature = 0,
            stream = true,
            messages = new[] { new { role = "user", content = "What is the answer?" } },
        });
        Assert.False(streamed.Headers.Contains("x-gateway-cache"));
    }

    [Fact]
    public async Task Answers_from_a_fallback_model_are_not_cached()
    {
        var client = WithCacheHeader(baseFactory.CreateAuthorizedClient(), "on");
        var request = new { model = "mock-down", messages = new[] { new { role = "user", content = "cache me?" } } };

        await client.PostAsJsonAsync("/v1/chat/completions", request);
        var second = await client.PostAsJsonAsync("/v1/chat/completions", request);

        Assert.Equal("miss", second.Headers.GetValues("x-gateway-cache").Single());
    }
}

public sealed class RedisResponseCacheTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task Stores_and_expires_entries()
    {
        var cache = new RedisResponseCache(redis.Connection);
        var entry = new CachedCompletion("{\"id\":\"x\"}", "gpt-4o-mini", 10, 20, 30);

        await cache.SetAsync("cache:{t}:k1", entry, TimeSpan.FromMilliseconds(300));
        Assert.Equal(entry, await cache.GetAsync("cache:{t}:k1"));
        Assert.Null(await cache.GetAsync("cache:{t}:other"));

        await Task.Delay(600);
        Assert.Null(await cache.GetAsync("cache:{t}:k1"));
    }
}
