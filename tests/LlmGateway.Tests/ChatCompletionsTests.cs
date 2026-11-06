using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LlmGateway.Tests.Infrastructure;

namespace LlmGateway.Tests;

public sealed class ChatCompletionsTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static object ChatRequest(string content, bool stream = false, bool includeUsage = false, string model = "mock-1")
    {
        var request = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "user", content } },
        };
        if (stream)
        {
            request["stream"] = true;
        }

        if (includeUsage)
        {
            request["stream_options"] = new { include_usage = true };
        }

        return request;
    }

    [Fact]
    public async Task Missing_api_key_returns_401()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", ChatRequest("hello"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("missing_api_key", error!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_api_key_returns_401()
    {
        var client = factory.CreateAuthorizedClient("gw_not_a_real_key");

        var response = await client.PostAsJsonAsync("/v1/chat/completions", ChatRequest("hello"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("invalid_api_key", error!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_model_returns_404()
    {
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", ChatRequest("hello", model: "no-such-model"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("model_not_found", error!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_json_returns_400()
    {
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsync("/v1/chat/completions",
            new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Non_streaming_request_returns_completion_and_records_usage()
    {
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", ChatRequest("hello"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completion = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("[mock] You said: hello",
            completion!["choices"]![0]!["message"]!["content"]!.GetValue<string>());

        var promptTokens = completion["usage"]!["prompt_tokens"]!.GetValue<int>();
        var completionTokens = completion["usage"]!["completion_tokens"]!.GetValue<int>();
        var expectedCost = promptTokens * 1 + completionTokens * 2; // mock-1: $1 in / $2 out per 1M tokens

        Assert.Equal(expectedCost.ToString(), response.Headers.GetValues("x-gateway-cost-micros").Single());

        var usage = await factory.WaitForUsageAsync(response.RequestId());
        Assert.Equal(GatewayFactory.TeamId, usage.TeamId);
        Assert.Equal("mock-1", usage.Model);
        Assert.Equal("mock", usage.Provider);
        Assert.Equal(promptTokens, usage.PromptTokens);
        Assert.Equal(completionTokens, usage.CompletionTokens);
        Assert.Equal(expectedCost, usage.CostMicros);
        Assert.Equal(200, usage.StatusCode);
        Assert.False(usage.Streamed);
        Assert.False(usage.UsageEstimated);
    }

    [Fact]
    public async Task Streaming_request_relays_chunks_and_hides_usage_chunk_client_did_not_ask_for()
    {
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", ChatRequest("stream me", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var events = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("data: [DONE]", events[^1]);
        Assert.DoesNotContain("\"usage\"", body);

        var text = string.Concat(events
            .Where(e => e != "data: [DONE]")
            .Select(e => JsonNode.Parse(e["data: ".Length..])!["choices"]![0]!["delta"]!["content"]?.GetValue<string>()));
        Assert.Equal("[mock] You said: stream me", text);

        // The gateway still billed real token counts from the usage chunk it requested upstream.
        var usage = await factory.WaitForUsageAsync(response.RequestId());
        Assert.True(usage.Streamed);
        Assert.False(usage.UsageEstimated);
        Assert.True(usage.PromptTokens > 0);
        Assert.True(usage.CompletionTokens > 0);
    }

    [Fact]
    public async Task Streaming_request_forwards_usage_chunk_when_client_asks_for_it()
    {
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions",
            ChatRequest("stream me", stream: true, includeUsage: true));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"usage\"", body);
    }

    [Fact]
    public async Task Provider_errors_are_passed_through_and_not_billed()
    {
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", ChatRequest("fail please [mock-error:503]"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("mock_503", error!["error"]!["code"]!.GetValue<string>());

        var usage = await factory.WaitForUsageAsync(response.RequestId());
        Assert.Equal(503, usage.StatusCode);
        Assert.Equal(0, usage.CostMicros);
    }

    [Fact]
    public async Task Models_endpoint_lists_configured_models()
    {
        var client = factory.CreateAuthorizedClient();

        var models = await client.GetFromJsonAsync<JsonObject>("/v1/models");

        var ids = models!["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("mock-1", ids);
        Assert.Contains("gpt-4o-mini", ids);
    }
}
