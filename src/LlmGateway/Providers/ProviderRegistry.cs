using LlmGateway.Config;
using Microsoft.Extensions.Options;

namespace LlmGateway.Providers;

public sealed record ModelRoute(string Model, string UpstreamModel, ModelOptions Options, IChatProvider Provider);

/// <summary>Maps public model names to the provider that serves them. Built once from configuration.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IChatProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModelOptions> _models = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(IOptions<GatewayOptions> options, IHttpClientFactory httpClientFactory)
    {
        foreach (var (name, provider) in options.Value.Providers)
        {
            _providers[name] = provider.Type.ToLowerInvariant() switch
            {
                "openai-compatible" => new OpenAiCompatibleProvider(name, provider, httpClientFactory),
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
    }

    public IEnumerable<(string Model, string Provider)> Models =>
        _models.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => (m.Key, m.Value.Provider));

    public bool TryResolve(string model, out ModelRoute route)
    {
        if (_models.TryGetValue(model, out var options))
        {
            route = new ModelRoute(model, options.UpstreamModel ?? model, options, _providers[options.Provider]);
            return true;
        }

        route = null!;
        return false;
    }
}
