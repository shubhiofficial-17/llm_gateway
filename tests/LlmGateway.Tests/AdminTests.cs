using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Tests.Infrastructure;

namespace LlmGateway.Tests;

public sealed class AdminTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private HttpClient AdminClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", GatewayFactory.AdminKey);
        return client;
    }

    [Fact]
    public async Task Admin_endpoints_require_admin_key()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", "wrong");

        var response = await client.PostAsJsonAsync("/admin/teams", new { id = "nope", name = "Nope" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_team_id_is_rejected()
    {
        var response = await AdminClient().PostAsJsonAsync("/admin/teams", new { id = "Not Valid!", name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task New_team_can_get_a_key_call_the_gateway_and_see_its_spend()
    {
        var admin = AdminClient();

        var team = await admin.PostAsJsonAsync("/admin/teams", new { id = "payments", name = "Payments" });
        Assert.Equal(HttpStatusCode.OK, team.StatusCode);

        var keyResponse = await admin.PostAsJsonAsync("/admin/teams/payments/keys", new { label = "ci" });
        var key = await keyResponse.Content.ReadFromJsonAsync<JsonObject>();
        var apiKey = key!["apiKey"]!.GetValue<string>();
        Assert.StartsWith("gw_", apiKey);

        var chat = await factory.CreateAuthorizedClient(apiKey).PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock-1",
            messages = new[] { new { role = "user", content = "hello" } },
        });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var usage = await factory.WaitForUsageAsync(chat.RequestId());
        Assert.Equal("payments", usage.TeamId);

        var summary = await admin.GetFromJsonAsync<JsonObject>("/admin/teams/payments/usage");
        Assert.Equal(1, summary!["requests"]!.GetValue<long>());
        Assert.Equal(usage.CostMicros, summary["costMicros"]!.GetValue<long>());
        Assert.Equal("mock-1", summary["byModel"]![0]!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Creating_a_key_for_unknown_team_returns_404()
    {
        var response = await AdminClient().PostAsJsonAsync("/admin/teams/ghost/keys", new { label = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
