using System.Text;
using LlmGateway.Chat;

namespace LlmGateway.Tests;

public sealed class SseRelayTests
{
    private static async Task<(string Output, SseRelayResult Result)> Relay(string input, bool forwardUsageOnlyChunks)
    {
        using var upstream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var downstream = new MemoryStream();
        var result = await SseRelay.RelayAsync(upstream, downstream, forwardUsageOnlyChunks, CancellationToken.None);
        return (Encoding.UTF8.GetString(downstream.ToArray()), result);
    }

    [Fact]
    public async Task Reads_usage_and_counts_completion_characters()
    {
        var (_, result) = await Relay(
            "data: {\"choices\":[{\"delta\":{\"content\":\"abcd\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"ef\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3}}\n\n" +
            "data: [DONE]\n\n",
            forwardUsageOnlyChunks: true);

        Assert.Equal(7, result.PromptTokens);
        Assert.Equal(3, result.CompletionTokens);
        Assert.Equal(6, result.CompletionChars);
        Assert.True(result.SawDone);
    }

    [Fact]
    public async Task Drops_usage_only_chunk_when_not_forwarding()
    {
        var (output, result) = await Relay(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3}}\n\n" +
            "data: [DONE]\n\n",
            forwardUsageOnlyChunks: false);

        Assert.Equal(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: [DONE]\n\n",
            output);
        Assert.Equal(7, result.PromptTokens); // still read for billing
    }

    [Fact]
    public async Task Preserves_comments_event_fields_and_crlf_input()
    {
        var (output, _) = await Relay(
            ": keep-alive\r\n\r\n" +
            "event: message\r\ndata: {\"choices\":[]}\r\n\r\n",
            forwardUsageOnlyChunks: false);

        Assert.Equal(": keep-alive\n\nevent: message\ndata: {\"choices\":[]}\n\n", output);
    }

    [Fact]
    public async Task Delivers_final_event_without_trailing_blank_line()
    {
        var (output, result) = await Relay("data: [DONE]", forwardUsageOnlyChunks: false);

        Assert.Equal("data: [DONE]\n\n", output);
        Assert.True(result.SawDone);
    }

    [Fact]
    public async Task Missing_usage_leaves_token_counts_null()
    {
        var (_, result) = await Relay(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n",
            forwardUsageOnlyChunks: false);

        Assert.Null(result.PromptTokens);
        Assert.Null(result.CompletionTokens);
        Assert.Equal(5, result.CompletionChars);
    }
}
