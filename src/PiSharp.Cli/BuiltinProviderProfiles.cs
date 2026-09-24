using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Versioned built-in profile metadata; transports and credentials remain provider-specific.</summary>
internal static class BuiltinProviderProfiles
{
    public static Dictionary<string, ProviderProfile> Create(Func<string, string?> environment)
    {
        var xaiDefault = environment("PISHARP_XAI_MODEL") ?? "grok-4.3";
        var xaiModels = new List<ModelDescriptor>();
        if (xaiDefault != "grok-4.3" && xaiDefault is not ("grok-4.5" or "grok-4.6" or "grok-4.7"))
            xaiModels.Add(new(xaiDefault, "xai", null, "configured", Provider: "xai", Api: "openai-responses"));
        xaiModels.Add(Xai("grok-4.3", "Grok 4.3", 1000000, 30000, 1.25m, 2.5m, 0.2m, 2.5m, 5m, 0.4m));
        xaiModels.Add(Xai("grok-4.5", "Grok 4.5", 500000, 500000, 2m, 6m, 0.3m, 4m, 12m, 0.6m));
        xaiModels.Add(Xai("grok-4.6", "Grok 4.6", 500000, 500000, 2m, 6m, 0.5m, 4m, 12m, 1m));
        xaiModels.Add(Xai("grok-4.7", "Grok 4.7", 500000, 500000, 2m, 6m, 0.5m, 4m, 12m, 1m));
        if (xaiDefault != xaiModels[0].Id)
        {
            var index = xaiModels.FindIndex(model => model.Id == xaiDefault);
            var chosen = xaiModels[index];
            xaiModels.RemoveAt(index);
            xaiModels.Insert(0, chosen);
        }
        var anthropicDefault = environment("PISHARP_ANTHROPIC_MODEL") ?? "claude-sonnet-4-6";
        var anthropicModels = new List<ModelDescriptor>
        {
            Anthropic("claude-sonnet-4-6", "Claude Sonnet 4.6", 1000000, 128000, 3m, 15m, 0.3m, 3.75m),
            Anthropic("claude-opus-4-6", "Claude Opus 4.6", 1000000, 128000, 5m, 25m, 0.5m, 6.25m),
            Anthropic("claude-haiku-4-5", "Claude Haiku 4.5 (latest)", 200000, 64000, 1m, 5m, 0.1m, 1.25m)
        };
        if (anthropicModels.FindIndex(model => model.Id == anthropicDefault) is int knownIndex && knownIndex >= 0)
        {
            var chosen = anthropicModels[knownIndex];
            anthropicModels.RemoveAt(knownIndex);
            anthropicModels.Insert(0, chosen);
        }
        else
            anthropicModels.Insert(0, new(anthropicDefault, "anthropic", null, "configured", Provider: "anthropic", Api: "anthropic-messages"));
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new("openai", "OpenAI", new Uri("https://api.openai.com/v1"), true, false,
                "OPENAI_API_KEY", null, [new(environment("PISHARP_MODEL") ?? "gpt-4o-mini", "openai", null, "catalog default", false, Provider: "openai")]),
            ["openrouter"] = new("openrouter", "OpenRouter", new Uri("https://openrouter.ai/api/v1"), true, false,
                "OPENROUTER_API_KEY", null, [new(environment("PISHARP_OPENROUTER_MODEL") ?? "openai/gpt-4o-mini", "openrouter", null, "catalog default", false, Provider: "openrouter")]),
            ["mistral"] = new("mistral", "Mistral", new Uri("https://api.mistral.ai/v1"), true, false,
                "MISTRAL_API_KEY", null, [new(environment("PISHARP_MISTRAL_MODEL") ?? "mistral-small-latest", "mistral", null, "catalog default", false, Provider: "mistral")]),
            ["anthropic"] = new("anthropic", "Anthropic", new Uri("https://api.anthropic.com"), true, false,
                "ANTHROPIC_API_KEY", null,
                anthropicModels),
            ["xai"] = new("xai", "xAI", new Uri("https://api.x.ai/v1"), true, false,
                "XAI_API_KEY", null, xaiModels)
        };
    }

    private static ModelDescriptor Anthropic(string id, string name, int context, int maxOutput,
        decimal input, decimal output, decimal cacheRead, decimal cacheWrite) =>
        new(id, "anthropic", context, "pinned catalogue", true,
            new ModelPricing(input, output, cacheRead, CachedWrite: cacheWrite), Provider: "anthropic",
            Name: name, MaxOutputTokens: maxOutput, Input: ["text", "image"], Api: "anthropic-messages");
    private static ModelDescriptor Xai(string id, string name, int context, int maxOutput,
        decimal input, decimal output, decimal cached, decimal tierInput, decimal tierOutput, decimal tierCached) =>
        new(id, "xai", context, "pinned catalogue", true,
            new ModelPricing(input, output, cached,
                [new ModelPricingTier(200000, tierInput, tierOutput, tierCached, 0m)], 0m),
            Provider: "xai", Name: name, MaxOutputTokens: maxOutput, Input: ["text", "image"], Api: "openai-responses");
}
