using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Config;
using LlmGateway.Providers;
using LlmGateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmGateway.Tests;

public sealed class FallbackTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    private static object Chat(string model, bool stream = false) => new
    {
        model,
        stream,
        messages = new[] { new { role = "user", content = "hello" } },
    };

    /// <summary>gpt-4o-mini (fake OpenAI upstream) falls back to the given models.</summary>
    private (WebApplicationFactory<Program> Factory, FakeUpstreamHandler Upstream) WithOpenAiFallingBackTo(params string[] fallbacks)
    {
        var upstream = new FakeUpstreamHandler();
        var settings = fallbacks
            .Select((f, i) => KeyValuePair.Create($"Gateway:Models:gpt-4o-mini:Fallbacks:{i}", (string?)f))
            .ToDictionary();
        var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(OpenAiCompatibleProvider.HttpClientName("openai"))
                    .ConfigurePrimaryHttpMessageHandler(() => upstream));
        });
        return (factory, upstream);
    }

    [Fact]
    public async Task Outage_falls_back_and_bills_the_model_that_answered()
    {
        // mock-down always returns 503 and falls back to mock-1 (see appsettings.json).
        var response = await baseFactory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Chat("mock-down"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mock-1", response.Headers.GetValues("x-gateway-model").Single());
        Assert.Equal("mock-down", response.Headers.GetValues("x-gateway-fallback-from").Single());
        var completion = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("[mock] You said: hello", completion!["choices"]![0]!["message"]!["content"]!.GetValue<string>());

        var usage = await baseFactory.WaitForUsageAsync(response.RequestId());
        Assert.Equal("mock-1", usage.Model);
        Assert.Equal("mock", usage.Provider);
    }

    [Fact]
    public async Task Streaming_requests_fall_back_too()
    {
        var response = await baseFactory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Chat("mock-down", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("mock-1", response.Headers.GetValues("x-gateway-model").Single());
        Assert.EndsWith("data: [DONE]\n\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Connection_failure_and_rate_limits_fall_back()
    {
        var (factory, upstream) = WithOpenAiFallingBackTo("mock-1");
        upstream.Respond = _ => throw new HttpRequestException("connection refused");

        var unreachable = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Chat("gpt-4o-mini"));
        Assert.Equal(HttpStatusCode.OK, unreachable.StatusCode);
        Assert.Equal("mock-1", unreachable.Headers.GetValues("x-gateway-model").Single());

        upstream.Respond = _ => FakeUpstreamHandler.Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"slow down"}}""");
        var limited = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Chat("gpt-4o-mini"));
        Assert.Equal(HttpStatusCode.OK, limited.StatusCode);
        Assert.Equal("mock-1", limited.Headers.GetValues("x-gateway-model").Single());
    }

    [Fact]
    public async Task Client_errors_are_not_retried_on_another_model()
    {
        var (factory, upstream) = WithOpenAiFallingBackTo("mock-1");
        upstream.Respond = _ => FakeUpstreamHandler.Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"bad temperature","code":"invalid_value"}}""");

        var response = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Chat("gpt-4o-mini"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(upstream.Requests);
        Assert.Equal("gpt-4o-mini", response.Headers.GetValues("x-gateway-model").Single());
        Assert.False(response.Headers.Contains("x-gateway-fallback-from"));
    }

    [Fact]
    public async Task When_every_model_fails_the_last_error_is_returned()
    {
        var (factory, upstream) = WithOpenAiFallingBackTo("mock-down");
        upstream.Respond = _ => throw new HttpRequestException("connection refused");

        var response = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Chat("gpt-4o-mini"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); // from mock-down, the last in the chain
        Assert.Equal("mock-down", response.Headers.GetValues("x-gateway-model").Single());
        var usage = await factory.WaitForUsageAsync(response.RequestId());
        Assert.Equal(0, usage.CostMicros);
    }

    [Fact]
    public void Unknown_or_self_referencing_fallbacks_are_rejected_at_startup()
    {
        static ProviderRegistry Build(params string[] fallbacks)
        {
            var options = new GatewayOptions
            {
                Providers = { ["mock"] = new ProviderOptions { Type = "mock" } },
                Models =
                {
                    ["a"] = new ModelOptions { Provider = "mock", Fallbacks = fallbacks.ToList() },
                    ["b"] = new ModelOptions { Provider = "mock" },
                },
            };
            return new ProviderRegistry(Options.Create(options), null!, NullLoggerFactory.Instance);
        }

        Assert.NotNull(Build("b"));
        Assert.Contains("unknown fallback", Assert.Throws<InvalidOperationException>(() => Build("nope")).Message);
        Assert.Contains("itself", Assert.Throws<InvalidOperationException>(() => Build("a")).Message);
    }
}
