using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using LlmGateway.Providers;
using LlmGateway.Providers.Anthropic;
using LlmGateway.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmGateway.Tests;

public sealed class AnthropicTranslatorTests
{
    [Fact]
    public void Request_moves_system_prompts_merges_turns_and_sets_max_tokens()
    {
        var openAi = JsonNode.Parse(
            """
            {
              "model": "claude-haiku-5-5",
              "temperature": 0.2,
              "stop": "END",
              "stream": true,
              "messages": [
                {"role": "system", "content": "Be brief."},
                {"role": "developer", "content": "Answer in English."},
                {"role": "user", "content": "Hi"},
                {"role": "user", "content": [{"type": "text", "text": "Are you there?"}]},
                {"role": "assistant", "content": "Yes."},
                {"role": "user", "content": "Good"}
              ]
            }
            """)!.AsObject();

        var anthropic = AnthropicTranslator.ToAnthropicRequest(openAi, "claude-haiku-5-5-20261007", defaultMaxTokens: 1024);

        Assert.Equal("claude-haiku-5-5-20261007", anthropic["model"]!.GetValue<string>());
        Assert.Equal(1024, anthropic["max_tokens"]!.GetValue<int>());
        Assert.Equal("Be brief.\n\nAnswer in English.", anthropic["system"]!.GetValue<string>());
        Assert.Equal(0.2, anthropic["temperature"]!.GetValue<double>());
        Assert.Equal("END", anthropic["stop_sequences"]![0]!.GetValue<string>());
        Assert.True(anthropic["stream"]!.GetValue<bool>());

        var messages = anthropic["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("Hi\n\nAre you there?", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("Good", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Client_max_tokens_wins_over_the_default()
    {
        var openAi = JsonNode.Parse("""{"max_tokens": 50, "messages": [{"role": "user", "content": "x"}]}""")!.AsObject();

        Assert.Equal(50, AnthropicTranslator.ToAnthropicRequest(openAi, "m", 1024)["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Completion_is_converted_with_usage_and_finish_reason()
    {
        var anthropic = JsonNode.Parse(
            """
            {
              "id": "msg_1", "type": "message", "role": "assistant",
              "content": [{"type": "text", "text": "Hello "}, {"type": "text", "text": "there"}],
              "stop_reason": "max_tokens",
              "usage": {"input_tokens": 12, "output_tokens": 7}
            }
            """)!.AsObject();

        var completion = AnthropicTranslator.ToOpenAiCompletion(anthropic, "claude-haiku-5-5");

        Assert.Equal("Hello there", completion["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal("length", completion["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Equal(12, completion["usage"]!["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(7, completion["usage"]!["completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Errors_are_converted_to_openai_shape()
    {
        var error = AnthropicTranslator.ToOpenAiError(
            """{"type":"error","error":{"type":"rate_limit_error","message":"Slow down"}}""", 429);

        Assert.Equal("Slow down", error["error"]!["message"]!.GetValue<string>());
        Assert.Equal("rate_limit_exceeded", error["error"]!["code"]!.GetValue<string>());
        Assert.Equal(503, AnthropicTranslator.ToOpenAiStatus(529));
        Assert.Equal("upstream_error", AnthropicTranslator.ToOpenAiError("<html>", 502)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stream_is_rewritten_as_openai_chunks_with_usage()
    {
        var sse = string.Join("\n",
            "event: message_start",
            """data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":25,"output_tokens":1}}}""",
            "",
            "event: content_block_start",
            """data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            "",
            "event: ping",
            """data: {"type":"ping"}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""",
            "",
            "event: content_block_delta",
            """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}""",
            "",
            "event: message_delta",
            """data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":15}}""",
            "",
            "event: message_stop",
            """data: {"type":"message_stop"}""",
            "");

        var pipe = new Pipe();
        await AnthropicTranslator.TranslateStreamAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)), pipe.Writer, "claude-haiku-5-5", includeUsage: true, CancellationToken.None);
        await pipe.Writer.CompleteAsync();
        var output = await new StreamReader(pipe.Reader.AsStream()).ReadToEndAsync();

        var events = output.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e["data: ".Length..]).ToList();
        Assert.Equal("[DONE]", events[^1]);
        var chunks = events.SkipLast(1).Select(e => JsonNode.Parse(e)!).ToList();

        var text = string.Concat(chunks
            .Where(c => c["choices"]!.AsArray().Count > 0)
            .Select(c => c["choices"]![0]!["delta"]!["content"]?.GetValue<string>()));
        Assert.Equal("Hello world", text);
        Assert.Contains(chunks, c => c["choices"]!.AsArray().Count > 0 && c["choices"]![0]!["finish_reason"]?.GetValue<string>() == "stop");

        var usage = chunks[^1]["usage"]!;
        Assert.Equal(25, usage["prompt_tokens"]!.GetValue<int>());
        Assert.Equal(15, usage["completion_tokens"]!.GetValue<int>());
    }
}

public sealed class AnthropicProviderTests(GatewayFactory baseFactory) : IClassFixture<GatewayFactory>
{
    private (WebApplicationFactory<Program> Factory, FakeUpstreamHandler Upstream) CreateGateway()
    {
        var upstream = new FakeUpstreamHandler();
        var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Providers:anthropic:ApiKey"] = "sk-ant-test",
            }));
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(OpenAiCompatibleProvider.HttpClientName("anthropic"))
                    .ConfigurePrimaryHttpMessageHandler(() => upstream));
        });
        return (factory, upstream);
    }

    [Fact]
    public async Task Streaming_request_goes_to_messages_api_and_is_billed_from_anthropic_usage()
    {
        var (factory, upstream) = CreateGateway();
        upstream.Respond = _ => FakeUpstreamHandler.EventStream(
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":1}}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi!\"}}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2000}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n");
        var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "claude-haiku-5-5",
            stream = true,
            messages = new[] { new { role = "user", content = "hello" } },
        });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"content\":\"Hi!\"", body);
        Assert.DoesNotContain("\"usage\"", body); // client didn't ask for the usage chunk
        Assert.EndsWith("data: [DONE]\n\n", body);

        var sent = Assert.Single(upstream.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Uri!.ToString());
        Assert.Equal("sk-ant-test", sent.Headers["x-api-key"]);
        Assert.Equal(AnthropicProvider.ApiVersion, sent.Headers["anthropic-version"]);
        Assert.Equal(1024, JsonNode.Parse(sent.Body)!["max_tokens"]!.GetValue<int>());

        var usage = await factory.WaitForUsageAsync(response.RequestId());
        Assert.Equal("anthropic", usage.Provider);
        Assert.Equal(1000, usage.PromptTokens);
        Assert.Equal(2000, usage.CompletionTokens);
        Assert.Equal(1100L, usage.CostMicros); // 1000 x $0.10 + 2000 x $0.50 per 1M
    }

    [Fact]
    public async Task Non_streaming_request_is_translated_both_ways()
    {
        var (factory, upstream) = CreateGateway();
        upstream.Respond = _ => FakeUpstreamHandler.Json(HttpStatusCode.OK,
            """{"id":"msg_2","type":"message","role":"assistant","content":[{"type":"text","text":"Paris."}],"stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":3}}""");

        var response = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "claude-sonnet-5-5",
            messages = new[] { new { role = "user", content = "Capital of France?" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var completion = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Paris.", completion!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.Equal(10, completion["usage"]!["prompt_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Overloaded_becomes_503_in_openai_error_shape()
    {
        var (factory, upstream) = CreateGateway();
        upstream.Respond = _ => FakeUpstreamHandler.Status(529,
            """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""");

        var response = await factory.CreateAuthorizedClient().PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "claude-haiku-5-5",
            messages = new[] { new { role = "user", content = "hi" } },
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("overloaded", error!["error"]!["code"]!.GetValue<string>());
    }
}
