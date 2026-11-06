using LlmGateway.Config;

namespace LlmGateway.Usage;

public static class Pricing
{
    /// <summary>
    /// Cost in micro-dollars. Prices are USD per 1M tokens, so
    /// tokens * price / 1,000,000 USD = tokens * price micro-dollars.
    /// </summary>
    public static long CostMicros(ModelOptions model, int promptTokens, int completionTokens)
    {
        var micros = promptTokens * model.InputPricePerMTok + completionTokens * model.OutputPricePerMTok;
        return (long)Math.Round(micros, MidpointRounding.AwayFromZero);
    }

    public static decimal MicrosToUsd(long micros) => micros / 1_000_000m;
}
