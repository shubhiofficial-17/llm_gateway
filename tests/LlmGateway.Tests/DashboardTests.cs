using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LlmGateway.Tests.Infrastructure;

namespace LlmGateway.Tests;

/// <summary>Keeps the Grafana dashboard in step with the metrics the gateway really exports.</summary>
public sealed partial class DashboardTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    [Fact]
    public async Task Every_gateway_metric_the_dashboard_queries_is_exported()
    {
        var client = factory.CreateAuthorizedClient();
        object Chat(string model, string content, bool stream = false, int? maxTokens = null) => maxTokens is null
            ? new { model, stream, messages = new[] { new { role = "user", content } } }
            : new { model, stream, max_tokens = maxTokens.Value, messages = new[] { new { role = "user", content } } };

        // Traffic that touches every instrument: a stream, a fallback, a limit rejection and a cache hit.
        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "stream", stream: true));
        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-down", "fallback"));
        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "too big", maxTokens: 1_000_000));
        client.DefaultRequestHeaders.Add("x-gateway-cache", "on");
        await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "cache"));
        var hit = await client.PostAsJsonAsync("/v1/chat/completions", Chat("mock-1", "cache"));
        await factory.WaitForUsageAsync(hit.RequestId());

        var exported = await factory.CreateClient().GetStringAsync("/metrics");

        var dashboard = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "llm-gateway.json")))!;
        var queried = dashboard["panels"]!.AsArray()
            .SelectMany(p => p!["targets"]?.AsArray() ?? new JsonArray())
            .Select(t => t!["expr"]!.GetValue<string>())
            .SelectMany(expr => MetricName().Matches(expr).Select(m => m.Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(queried);
        Assert.All(queried, metric => Assert.Contains(metric, exported));
    }

    [GeneratedRegex(@"gateway_[a-z_]+")]
    private static partial Regex MetricName();
}
