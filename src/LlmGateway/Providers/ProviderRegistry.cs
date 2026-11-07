using LlmGateway.Config;
using LlmGateway.Providers.Anthropic;
using Microsoft.Extensions.Options;

namespace LlmGateway.Providers;

public sealed record ModelRoute(string Model, string UpstreamModel, ModelOptions Options, IChatProvider Provider)
{
    /// <summary>Models to try, in order, if this one's provider fails. See ADR 0009.</summary>
    public IReadOnlyList<ModelRoute> Fallbacks { get; init; } = [];

    /// <summary>This model followed by its fallbacks.</summary>
    public IEnumerable<ModelRoute> Chain => Fallbacks.Prepend(this);
}

/// <summary>Maps public model names to the provider that serves them. Built once from configuration.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelOptions> _models = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(IOptions<GatewayOptions> options, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        foreach (var (name, provider) in options.Value.Providers)
        {
            _providers[name] = provider.Type.ToLowerInvariant() switch
            {
                "openai-compatible" => new OpenAiCompatibleProvider(name, provider, httpClientFactory),
                "anthropic" => new AnthropicProvider(name, provider, httpClientFactory, loggerFactory.CreateLogger<AnthropicProvider>()),
                "mock" => new MockProvider(name, provider),
                var type => throw new InvalidOperationException($"Provider '{name}' has unknown type '{type}'."),
            };
        }

        foreach (var (name, model) in options.Value.Models)
        {
            if (!_providers.ContainsKey(model.Provider))
            {
                throw new InvalidOperationException($"Model '{name}' references unknown provider '{model.Provider}'.");
            }

            _models[name] = model;
        }

        foreach (var (name, model) in _models)
        {
            foreach (var fallback in model.Fallbacks)
            {
                if (!_models.ContainsKey(fallback))
                {
                    throw new InvalidOperationException($"Model '{name}' has unknown fallback '{fallback}'.");
                }

                if (string.Equals(fallback, name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Model '{name}' lists itself as a fallback.");
                }
            }
        }
    }

    private ModelRoute Route(string model, ModelOptions options) =>
        new(model, options.UpstreamModel ?? model, options, _providers[options.Provider]);

    public static bool UsesHttp(ProviderOptions provider) =>
        provider.Type.ToLowerInvariant() is "openai-compatible" or "anthropic";

    public IEnumerable<string> ProviderNames => _providers.Keys.Order(StringComparer.Ordinal);

    public IEnumerable<(string Model, string Provider)> Models =>
        _models.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => (m.Key, m.Value.Provider));

    public bool TryResolve(string model, out ModelRoute route)
    {
        if (_models.TryGetValue(model, out var options))
        {
            // Fallbacks are one level deep: a fallback's own fallbacks are not followed.
            route = Route(model, options) with
            {
                Fallbacks = options.Fallbacks.Select(f => Route(f, _models[f])).ToList(),
            };
            return true;
        }

        route = null!;
        return false;
    }
}
