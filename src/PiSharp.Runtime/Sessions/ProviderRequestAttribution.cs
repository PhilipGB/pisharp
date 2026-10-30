using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Tracks physical request identity and billing across routing and streamed usage events.</summary>
internal sealed class ProviderRequestAttribution(string model, string? provider, string? api,
    ModelPricing? pricing, string? thinkingLevel)
{
    public string Model { get; private set; } = model;
    public string? Provider { get; private set; } = provider;
    public string? Api { get; private set; } = api;
    public ModelPricing? Pricing { get; private set; } = pricing;
    public string? ThinkingLevel { get; private set; } = thinkingLevel;

    public void Begin(string model, string? provider, string? api, ModelPricing? pricing)
    {
        Model = model;
        Provider = provider;
        Api = api;
        Pricing = pricing;
    }

    public void Route(AgentLifecycleEvent item)
    {
        Model = item.ProviderModelId ?? Model;
        Provider = item.ProviderProviderId ?? Provider;
        Pricing = item.ProviderPricing;
        ThinkingLevel = item.ProviderThinkingLevel;
        Api = item.ProviderApi ?? Api;
    }

    public ChatMessage? AttributeResponse(AgentLifecycleEvent item, string? logicalThinkingLevel)
    {
        var response = item.ProviderResponse;
        if (response is null) return null;
        var attributed = item.ProviderProviderId is null && Api is not null
            ? ChatMessageProperties.WithProviderIdentity(response, Provider, Model, Api, logicalThinkingLevel)
            : response;
        if (string.IsNullOrWhiteSpace(item.ProviderResponseId) &&
            string.IsNullOrWhiteSpace(item.ProviderFinishReason)) return attributed;

        var persisted = attributed.Clone();
        persisted.AdditionalProperties = attributed.AdditionalProperties?.Clone() ?? new();
        if (!string.IsNullOrWhiteSpace(item.ProviderResponseId))
            persisted.AdditionalProperties["pisharp.responseId"] = item.ProviderResponseId;
        if (!string.IsNullOrWhiteSpace(item.ProviderFinishReason))
            persisted.AdditionalProperties["pisharp.rawStopReason"] = item.ProviderFinishReason;
        return persisted;
    }

    public UsageRecord Usage(UsageDetails usage) => UsageRecord.Create(Model, "model", usage, Pricing);

    public AgentLifecycleEvent Complete(AgentLifecycleEvent item, ChatMessage? response) => item with
    {
        ProviderResponse = response,
        ProviderThinkingLevel = ThinkingLevel,
        UsageSnapshot = item.ProviderUsage is { } usage ? Usage(usage) : null
    };
}
