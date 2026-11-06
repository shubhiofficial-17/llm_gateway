using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Providers;
using LlmGateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests;

/// <summary>Exercises the real HTTP provider path against a fake upstream.</summary>
public sealed class OpenAiCompatibleProviderTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    private (WebApplicationFactory<Program> Factory, FakeUpstreamHandler Upstream) CreateGateway()
    {
        var upstream = new FakeUpstreamHandler();
        var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Providers:openai:ApiKey"] = "sk-test",
                ["Gateway:Models:gpt-4o-mini:UpstreamModel"] = "gpt-4o-mini-2024-07-18",
            }));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(OpenAiCompatibleProvider.HttpClientName("openai"))
                    .ConfigurePrimaryHttpMessageHandler(() => upstream));
        });
        return (factory, upstream);
    }

    private static object StreamingRequest() => new
    {
        model = "gpt-4o-mini",
        stream = true,
        messages = new[] { new { role = "user", content = "hi" } },
    };

    [Fact]
    public async Task Streaming_request_is_rewritten_for_upstream_and_billed_from_usage_chunk()
    {
        var (factory, upstream) = CreateGateway();
        upstream.Respond = _ => FakeUpstreamHandler.EventStream(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":2000}}\n\n" +
            "data: [DONE]\n\n");
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", StreamingRequest());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hel", body);
        Assert.DoesNotContain("\"usage\"", body);
        Assert.EndsWith("data: [DONE]\n\n", body);

        var sent = Assert.Single(upstream.Requests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", sent.Uri!.ToString());
        Assert.Equal("Bearer sk-test", sent.Authorization);
        var sentBody = JsonNode.Parse(sent.Body)!;
        Assert.Equal("gpt-4o-mini-2024-07-18", sentBody["model"]!.GetValue<string>());
        Assert.True(sentBody["stream_options"]!["include_usage"]!.GetValue<bool>());

        var usage = await factory.WaitForUsageAsync(response.RequestId());
        Assert.Equal("gpt-4o-mini", usage.Model);
        Assert.Equal(1000, usage.PromptTokens);
        Assert.Equal(2000, usage.CompletionTokens);
        Assert.Equal(1350L, usage.CostMicros); // 1000 x $0.15 + 2000 x $0.60 per 1M = 150 + 1200 micro-USD
        Assert.False(usage.UsageEstimated);
    }

    [Fact]
    public async Task Upstream_rate_limit_is_passed_through()
    {
        var (factory, upstream) = CreateGateway();
        upstream.Respond = _ => FakeUpstreamHandler.Json(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"message\":\"Rate limit reached\",\"type\":\"requests\",\"code\":\"rate_limit_exceeded\"}}");
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", StreamingRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("rate_limit_exceeded", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unreachable_upstream_returns_502()
    {
        var (factory, upstream) = CreateGateway();
        upstream.Respond = _ => throw new HttpRequestException("connection refused");
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", StreamingRequest());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("upstream_unreachable", error!["error"]!["code"]!.GetValue<string>());
    }
}
