using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Tests.Infrastructure;
using LlmGateway.Usage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests;

public sealed class BudgetEndpointTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    // mock-1 costs $1 per 1M input and $2 per 1M output tokens, i.e. 1 and 2 micro-USD per token.
    // "hi" with max_tokens 10 reserves 5 + 2*10 = 25 micro-USD and really costs 5 + 2*5 = 15.
    private static readonly object Request = new
    {
        model = "mock-1",
        max_tokens = 10,
        messages = new[] { new { role = "user", content = "hi" } },
    };

    private WebApplicationFactory<Program> WithBudget(string usd) =>
        baseFactory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:Budgets:DefaultMonthlyBudgetUsd"] = usd })));

    [Fact]
    public async Task Requests_stop_with_402_once_the_monthly_budget_is_used()
    {
        var factory = WithBudget("0.0001"); // 100 micro-USD
        var client = factory.CreateAuthorizedClient();

        // Each request settles to 15, and the next needs 25 of headroom: 6 fit (6 x 15 = 90).
        for (var i = 0; i < 6; i++)
        {
            var ok = await client.PostAsJsonAsync("/v1/chat/completions", Request);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var usage = await factory.WaitForUsageAsync(ok.RequestId());
            Assert.Equal(15, usage.CostMicros);
        }

        var refused = await client.PostAsJsonAsync("/v1/chat/completions", Request);

        Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
        Assert.Equal("0.00001", refused.Headers.GetValues("x-gateway-budget-remaining-usd").Single());
        var error = await refused.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("budget_exceeded", error!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Spend_already_in_the_ledger_counts_against_the_budget()
    {
        var factory = WithBudget("0.0001");
        var ledger = factory.Services.GetRequiredService<IUsageStore>();
        await ledger.WriteBatchAsync(
        [
            new UsageEvent("req_earlier", GatewayFactory.TeamId, "mock-1", "mock", 30, 30, 90, 200, 10, false, false, DateTimeOffset.UtcNow),
        ], CancellationToken.None);

        var response = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Request);

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task Admin_can_set_a_team_budget_and_free_models_are_not_blocked()
    {
        var factory = WithBudget("0");
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", GatewayFactory.AdminKey);

        var set = await admin.PutAsJsonAsync($"/admin/teams/{GatewayFactory.TeamId}/limits", new { monthlyBudgetUsd = 0.00002m });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(20, (await set.Content.ReadFromJsonAsync<JsonObject>())!["monthlyBudgetMicros"]!.GetValue<long>());

        var response = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Request);
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode); // needs 25, budget is 20
    }

    [Fact]
    public async Task No_budget_means_no_budget_header()
    {
        var response = await baseFactory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", Request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("x-gateway-budget-remaining-usd"));
    }
}
