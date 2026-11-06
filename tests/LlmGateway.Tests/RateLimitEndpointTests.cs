using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Auth;
using LlmGateway.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests;

public sealed class RateLimitEndpointTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    private static readonly object Request = new
    {
        model = "mock-1",
        messages = new[] { new { role = "user", content = "hi" } },
    };

    [Fact]
    public async Task Team_over_its_requests_per_minute_gets_429_with_retry_after()
    {
        var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:RateLimits:RequestsPerMinute"] = "2" })));
        var client = factory.CreateAuthorizedClient();

        var first = await client.PostAsJsonAsync("/v1/chat/completions", Request);
        var second = await client.PostAsJsonAsync("/v1/chat/completions", Request);
        var third = await client.PostAsJsonAsync("/v1/chat/completions", Request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("2", first.Headers.GetValues("x-ratelimit-limit-requests").Single());
        Assert.Equal("1", first.Headers.GetValues("x-ratelimit-remaining-requests").Single());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), third.Headers.RetryAfter!.Delta); // 2/min refills one every 30s
        var error = await third.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("rate_limit_exceeded", error!["error"]!["code"]!.GetValue<string>());

        // Another team has its own budget of requests.
        var teams = factory.Services.GetRequiredService<ITeamStore>();
        await teams.UpsertTeamAsync(new Team("other-team", "Other"), CancellationToken.None);
        await teams.AddKeyAsync("other-team", ApiKeys.Hash("gw_other_key"), null, CancellationToken.None);
        var other = await factory.CreateAuthorizedClient("gw_other_key").PostAsJsonAsync("/v1/chat/completions", Request);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Zero_turns_the_limit_off()
    {
        var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:RateLimits:RequestsPerMinute"] = "0" })));
        var client = factory.CreateAuthorizedClient();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/v1/chat/completions", Request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("x-ratelimit-limit-requests"));
        }
    }

    [Fact]
    public async Task Admin_can_give_one_team_its_own_limit()
    {
        var factory = baseFactory.WithWebHostBuilder(_ => { });
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", GatewayFactory.AdminKey);
        await admin.PostAsJsonAsync("/admin/teams", new { id = "batch-jobs", name = "Batch jobs" });
        var key = (await (await admin.PostAsJsonAsync("/admin/teams/batch-jobs/keys", new { label = "t" }))
            .Content.ReadFromJsonAsync<JsonObject>())!["apiKey"]!.GetValue<string>();

        var set = await admin.PutAsJsonAsync("/admin/teams/batch-jobs/limits", new { requestsPerMinute = 1 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var team = await admin.GetFromJsonAsync<JsonObject>("/admin/teams/batch-jobs");
        Assert.Equal(1, team!["requestsPerMinute"]!.GetValue<int>());

        var client = factory.CreateAuthorizedClient(key);
        var first = await client.PostAsJsonAsync("/v1/chat/completions", Request);
        var second = await client.PostAsJsonAsync("/v1/chat/completions", Request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("1", first.Headers.GetValues("x-ratelimit-limit-requests").Single());
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        // The default (60/min) still applies to everyone else.
        var other = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Request);
        Assert.Equal("60", other.Headers.GetValues("x-ratelimit-limit-requests").Single());
    }

    [Fact]
    public async Task Limits_endpoint_rejects_negative_values_and_unknown_teams()
    {
        var admin = baseFactory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", GatewayFactory.AdminKey);

        var negative = await admin.PutAsJsonAsync($"/admin/teams/{GatewayFactory.TeamId}/limits", new { requestsPerMinute = -1 });
        var unknown = await admin.PutAsJsonAsync("/admin/teams/ghost/limits", new { requestsPerMinute = 5 });

        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
