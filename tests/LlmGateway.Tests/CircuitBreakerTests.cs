using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Providers;
using LlmGateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LlmGateway.Tests;

public sealed class CircuitBreakerTests
{
    private readonly FakeTimeProvider _clock = new();

    private CircuitBreakers Create(int threshold = 3, int breakSeconds = 30)
    {
        var options = new CircuitBreakerOptions { FailureThreshold = threshold, BreakSeconds = breakSeconds };
        return new CircuitBreakers(new StaticOptionsMonitor<CircuitBreakerOptions>(options), _clock, NullLogger<CircuitBreakers>.Instance);
    }

    [Fact]
    public void Opens_after_consecutive_failures_and_rejects_until_the_break_ends()
    {
        var breakers = Create(threshold: 3);

        breakers.RecordFailure("p");
        breakers.RecordFailure("p");
        Assert.True(breakers.TryEnter("p", out _));

        breakers.RecordFailure("p");
        Assert.Equal(CircuitState.Open, breakers.Health("p").State);
        Assert.False(breakers.TryEnter("p", out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(30), retryAfter);

        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(breakers.TryEnter("p", out retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(20), retryAfter);
    }

    [Fact]
    public void A_success_resets_the_failure_count()
    {
        var breakers = Create(threshold: 3);

        breakers.RecordFailure("p");
        breakers.RecordFailure("p");
        breakers.RecordSuccess("p");
        breakers.RecordFailure("p");
        breakers.RecordFailure("p");

        Assert.Equal(CircuitState.Closed, breakers.Health("p").State);
    }

    [Fact]
    public void Half_open_lets_one_trial_through_and_its_result_decides()
    {
        var breakers = Create(threshold: 1);
        breakers.RecordFailure("p");
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breakers.TryEnter("p", out _));   // the trial
        Assert.Equal(CircuitState.HalfOpen, breakers.Health("p").State);
        Assert.False(breakers.TryEnter("p", out _));  // others wait for it

        breakers.RecordFailure("p");                   // trial failed: open again for a full break
        Assert.Equal(CircuitState.Open, breakers.Health("p").State);
        Assert.False(breakers.TryEnter("p", out _));

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(breakers.TryEnter("p", out _));
        breakers.RecordSuccess("p");                   // trial succeeded: closed
        Assert.Equal(CircuitState.Closed, breakers.Health("p").State);
        Assert.True(breakers.TryEnter("p", out _));
    }

    [Fact]
    public void An_abandoned_or_lost_trial_does_not_block_the_provider_forever()
    {
        var breakers = Create(threshold: 1);
        breakers.RecordFailure("p");
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.True(breakers.TryEnter("p", out _));
        breakers.Abandon("p");                         // client cancelled the trial
        Assert.True(breakers.TryEnter("p", out _));    // another trial may go

        _clock.Advance(TimeSpan.FromSeconds(30));      // that trial never reported back
        Assert.True(breakers.TryEnter("p", out _));
    }

    [Fact]
    public void Threshold_zero_turns_breaking_off()
    {
        var breakers = Create(threshold: 0);
        for (var i = 0; i < 10; i++)
        {
            breakers.RecordFailure("p");
        }

        Assert.True(breakers.TryEnter("p", out _));
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

public sealed class CircuitBreakerEndpointTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    private WebApplicationFactory<Program> WithThreshold(int threshold) =>
        baseFactory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Gateway:CircuitBreaker:FailureThreshold"] = threshold.ToString() })));

    private static object Chat(string model, string content = "hello") => new
    {
        model,
        messages = new[] { new { role = "user", content } },
    };

    [Fact]
    public async Task Failing_provider_is_skipped_once_its_circuit_opens()
    {
        var factory = WithThreshold(2);
        var client = factory.CreateAuthorizedClient();
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", GatewayFactory.AdminKey);

        // Two outages through the fallback open mock-outage's circuit.
        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-down"));
            Assert.Equal("mock-1", response.Headers.GetValues("x-gateway-model").Single());
        }

        var providers = await admin.GetFromJsonAsync<JsonArray>("/admin/providers");
        var outage = providers!.Single(p => p!["provider"]!.GetValue<string>() == "mock-outage")!;
        Assert.Equal("Open", outage["state"]!.GetValue<string>());
        Assert.Equal(2, outage["consecutiveFailures"]!.GetValue<int>());

        // Still answered by the fallback, and mock-outage isn't called again (its failure count stays at 2).
        var skipped = await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-down"));
        Assert.Equal(HttpStatusCode.OK, skipped.StatusCode);
        Assert.Equal("mock-1", skipped.Headers.GetValues("x-gateway-model").Single());
        providers = await admin.GetFromJsonAsync<JsonArray>("/admin/providers");
        Assert.Equal(2, providers!.Single(p => p!["provider"]!.GetValue<string>() == "mock-outage")!["consecutiveFailures"]!.GetValue<int>());
    }

    [Fact]
    public async Task Model_without_fallbacks_fails_fast_with_retry_after_while_open()
    {
        var factory = WithThreshold(2);
        var client = factory.CreateAuthorizedClient();

        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "[mock-error:500]"));
        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "[mock-error:502]"));

        var response = await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("provider_unavailable", error!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Client_errors_do_not_count_as_provider_failures()
    {
        var factory = WithThreshold(2);
        var client = factory.CreateAuthorizedClient();

        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "[mock-error:400]"));
        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "[mock-error:400]"));
        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "[mock-error:429]"));

        var response = await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
