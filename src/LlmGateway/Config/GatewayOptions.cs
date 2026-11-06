namespace LlmGateway.Config;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Key required in the X-Admin-Key header for /admin endpoints. Empty disables them.</summary>
    public string? AdminKey { get; set; }

    public Dictionary<string, ProviderOptions> Providers { get; set; } = new();

    /// <summary>Public model names clients may request, mapped to a provider and prices.</summary>
    public Dictionary<string, ModelOptions> Models { get; set; } = new();

    public BootstrapOptions Bootstrap { get; set; } = new();
}

public sealed class ProviderOptions
{
    /// <summary>"openai-compatible" (OpenAI, Ollama, Groq, vLLM...) or "mock".</summary>
    public string Type { get; set; } = "openai-compatible";

    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Mock provider only: delay between streamed chunks, to make streaming visible in demos.</summary>
    public int MockTokenDelayMs { get; set; }
}

public sealed class ModelOptions
{
    public string Provider { get; set; } = "";

    /// <summary>Model name sent upstream. Defaults to the public model name.</summary>
    public string? UpstreamModel { get; set; }

    /// <summary>USD per 1M input tokens.</summary>
    public decimal InputPricePerMTok { get; set; }

    /// <summary>USD per 1M output tokens.</summary>
    public decimal OutputPricePerMTok { get; set; }
}

/// <summary>Optional team and API key created on startup, for local demos.</summary>
public sealed class BootstrapOptions
{
    public string? TeamId { get; set; }
    public string? TeamName { get; set; }
    public string? ApiKey { get; set; }
}
