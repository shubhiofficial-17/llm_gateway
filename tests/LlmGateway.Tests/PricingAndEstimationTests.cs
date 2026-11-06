using System.Text.Json.Nodes;
using LlmGateway.Config;
using LlmGateway.Usage;

namespace LlmGateway.Tests;

public sealed class PricingAndEstimationTests
{
    [Theory]
    [InlineData(1_000_000, 0, 150_000L)]   // 1M input tokens at $0.15 = $0.15
    [InlineData(0, 1_000_000, 600_000L)]   // 1M output tokens at $0.60 = $0.60
    [InlineData(1000, 2000, 1350L)]
    [InlineData(1, 1, 1L)]                 // 0.75 micro-USD rounds to 1
    [InlineData(0, 0, 0L)]
    public void Cost_is_computed_in_integer_micro_dollars(int prompt, int completion, long expectedMicros)
    {
        var model = new ModelOptions { InputPricePerMTok = 0.15m, OutputPricePerMTok = 0.60m };

        Assert.Equal(expectedMicros, Pricing.CostMicros(model, prompt, completion));
    }

    [Fact]
    public void Micros_convert_to_usd()
    {
        Assert.Equal(1.5m, Pricing.MicrosToUsd(1_500_000));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    public void Token_estimate_rounds_up_four_chars_per_token(string text, int expected)
    {
        Assert.Equal(expected, TokenEstimator.Estimate(text));
    }

    [Fact]
    public void Message_estimate_handles_string_and_multipart_content()
    {
        var request = JsonNode.Parse(
            """
            {"messages":[
              {"role":"system","content":"abcd"},
              {"role":"user","content":[{"type":"text","text":"abcdefgh"},{"type":"image_url","image_url":{"url":"x"}}]}
            ]}
            """)!.AsObject();

        // (4 overhead + 1) + (4 overhead + 2)
        Assert.Equal(11, TokenEstimator.EstimateMessages(request));
    }
}
