using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Codemode;

namespace PiSharp.Cli;

public sealed class ProviderCodemodeModels(ProviderModelRuntime providers, HttpClient http) : ICodemodeModels
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly ProviderClassifierRuntime _classifiers = new(providers, http);

    public IReadOnlyList<JsonElement> GetModels(string type, string? provider = null)
    {
        var profiles = provider is null ? providers.Providers : [providers.GetProvider(provider)];
        return type switch
        {
            "classifier" => _classifiers.ListModels(provider).Select(model => JsonSerializer.SerializeToElement(new
            {
                type,
                model.Provider,
                model.Id,
                model.Api,
                baseUrl = model.BaseUrl.AbsoluteUri,
                model.ContextWindow,
                cost = model.Pricing
            }, s_json)).ToArray(),
            "chat" => profiles.SelectMany(profile => profile.Models.Select(model => ChatModel(model, profile)))
                .Concat((providers.VirtualModels?.Models ?? []).Where(definition => provider is null || definition.Model.Provider == provider)
                    .Select(definition => ChatModel(definition.Model, providers.GetProvider(definition.Model.Provider!))))
                .DistinctBy(model => (model.GetProperty("provider").GetString(), model.GetProperty("id").GetString())).ToArray(),
            "image" => [],
            _ => throw new ArgumentException("Unknown model type.")
        };
    }

    public async Task<IReadOnlyList<JsonElement>> GetAvailableAsync(string type, string? provider, CancellationToken cancellationToken)
    {
        if (type == "chat")
        {
            var models = await providers.ListModelsAsync(provider, cancellationToken, includeOutOfScope: true);
            return models.Where(model => model.Available).Select(model => ChatModel(model, providers.GetProvider(model.Provider!))).ToArray();
        }
        var catalog = GetModels(type, provider);
        var available = new List<JsonElement>();
        foreach (var group in catalog.GroupBy(model => model.GetProperty("provider").GetString()!))
        {
            var auth = await providers.ResolveClassifierAccessAsync(group.Key, cancellationToken);
            if (!auth.Authenticated) continue;
            foreach (var model in group)
            {
                if (group.Key == "cloudflare-workers-ai" && (!auth.Environment.TryGetValue("CLOUDFLARE_ACCOUNT_ID", out var account) || string.IsNullOrEmpty(account))) continue;
                available.Add(model);
            }
        }
        return available;
    }

    public Task<ClassifierResult> ClassifyAsync(string provider, string id, ClassifierContext context, CancellationToken cancellationToken) =>
        _classifiers.ClassifyAsync(provider, id, context, cancellationToken);

    private static JsonElement ChatModel(Runtime.Providers.ModelDescriptor model, ProviderProfile profile) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "chat",
            provider = profile.Id,
            model.Id,
            name = model.Name ?? model.Id,
            api = ProviderChatClientFactory.GetProtocolId(profile, model),
            baseUrl = model.BaseUrl ?? profile.Endpoint.AbsoluteUri,
            reasoning = model.Reasoning ?? false,
            input = model.Input ?? ["text"],
            model.InputLimits,
            contextWindow = model.ContextLength ?? 0,
            maxTokens = model.MaxOutputTokens ?? 0,
            cost = Price(model.Pricing),
            model.ThinkingLevelMap,
            model.PromptCache,
            samplingParams = model.SamplingParameters,
            compat = model.Compatibility
        }, s_json);

    private static JsonNode? Price(Runtime.Sessions.ModelPricing? price) => price is null ? null :
        JsonSerializer.SerializeToNode(new
        {
            price.Input,
            price.Output,
            cacheRead = price.CachedInput,
            cacheWrite = price.CachedWrite,
            tiers = price.Tiers?.Select(tier => new
            {
                tier.InputTokensAbove,
                tier.Input,
                tier.Output,
                cacheRead = tier.CachedInput,
                cacheWrite = tier.CachedWrite
            }).ToArray()
        }, s_json);
}
