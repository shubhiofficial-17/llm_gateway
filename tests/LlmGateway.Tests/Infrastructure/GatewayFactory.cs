using System.Net.Http.Headers;
using LlmGateway.Usage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests.Infrastructure;

/// <summary>Runs the gateway in-memory with in-memory stores and a bootstrapped test team.</summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "gw_test_key";
    public const string AdminKey = "test-admin-key";
    public const string TeamId = "test-team";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:AdminKey"] = AdminKey,
            ["Gateway:Bootstrap:TeamId"] = TeamId,
            ["Gateway:Bootstrap:TeamName"] = "Test team",
            ["Gateway:Bootstrap:ApiKey"] = ApiKey,
        }));
    }
}

public static class GatewayTestExtensions
{
    public static HttpClient CreateAuthorizedClient(this WebApplicationFactory<Program> factory, string apiKey = GatewayFactory.ApiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    /// <summary>Usage is written by a background service, so wait for the event to land.</summary>
    public static async Task<UsageEvent> WaitForUsageAsync(this WebApplicationFactory<Program> factory, string requestId)
    {
        var store = (InMemoryUsageStore)factory.Services.GetRequiredService<IUsageStore>();
        for (var i = 0; i < 100; i++)
        {
            var match = store.Events.FirstOrDefault(e => e.RequestId == requestId);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No usage event recorded for {requestId}.");
    }

    public static string RequestId(this HttpResponseMessage response) =>
        response.Headers.GetValues("x-request-id").Single();
}
