using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace LlmGateway.Tests;

public sealed class TokenLimitTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    private WebApplicationFactory<Program> WithTokensPerMinute(int tpm) =>
        baseFactory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:RateLimits:TokensPerMinute"] = tpm.ToString() })));

    private static object Request(int maxTokens) => new
    {
        model = "mock-1",
        max_tokens = maxTokens,
        messages = new[] { new { role = "user", content = "hi" } },
    };

    [Fact]
    public async Task Unused_reservation_is_refunded_after_the_response()
    {
        var factory = WithTokensPerMinute(100);
        var client = factory.CreateAuthorizedClient();

        // Prompt estimate 5 + max_tokens 90 = 95 reserved; the mock really uses about 10.
        var first = await client.PostAsJsonAsync("/v1/chat/completions", Request(90));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("100", first.Headers.GetValues("x-ratelimit-limit-tokens").Single());
        Assert.Equal("5", first.Headers.GetValues("x-ratelimit-remaining-tokens").Single());
        var usage = await factory.WaitForUsageAsync(first.RequestId()); // settle happens before usage is recorded

        // About 85 tokens came back, so ~90 are left: not enough for another 95.
        var second = await client.PostAsJsonAsync("/v1/chat/completions", Request(90));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var remaining = int.Parse(second.Headers.GetValues("x-ratelimit-remaining-tokens").Single());
        Assert.InRange(remaining, 100 - usage.PromptTokens - usage.CompletionTokens, 100);
        Assert.NotNull(second.Headers.RetryAfter);
        var error = await second.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("rate_limit_exceeded", error!["error"]!["code"]!.GetValue<string>());

        // A smaller request still fits.
        var third = await client.PostAsJsonAsync("/v1/chat/completions", Request(20));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task Request_that_can_never_fit_gets_400_not_429()
    {
        var client = WithTokensPerMinute(100).CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", Request(1_000));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("request_too_large", error!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Failed_upstream_requests_refund_everything()
    {
        var factory = WithTokensPerMinute(100);
        var client = factory.CreateAuthorizedClient();

        var failed = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock-1",
            max_tokens = 90,
            messages = new[] { new { role = "user", content = "[mock-error:500]" } },
        });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await factory.WaitForUsageAsync(failed.RequestId());

        var next = await client.PostAsJsonAsync("/v1/chat/completions", Request(90));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }
}
