using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Runtime.Sessions;

public sealed record VirtualModelFailedRequest(string Provider, ModelDescriptor Model, string ThinkingLevel,
    ChatMessage Message, string? Error);

public sealed record VirtualModelRequestContext(ConversationSession Session, IReadOnlyList<ChatMessage> Messages,
    string Reason, string ThinkingLevel, VirtualModelFailedRequest? Failed, CancellationToken CancellationToken);

public sealed record VirtualModelRequestRoute(IChatClient ChatClient, ModelDescriptor Model, string Provider,
    string ThinkingLevel, ReasoningOptions? Reasoning, ModelPricing? Pricing,
    AutoCompactionPolicy? ContextPolicy, JsonElement? State = null,
    string? LogicalProvider = null, string? LogicalModel = null);

public delegate Task<VirtualModelRequestRoute> VirtualModelRequestRouter(VirtualModelRequestContext request);

internal sealed record VirtualModelRequestHint(string Reason, string ThinkingLevel,
    VirtualModelFailedRequest? Failed = null);

internal static class VirtualModelRequestHints
{
    public const string PropertyName = "pisharp.virtualModelRequest";

    public static ChatOptions WithHint(ChatOptions? options, VirtualModelRequestHint hint)
    {
        var copy = options?.Clone() ?? new ChatOptions();
        var properties = new AdditionalPropertiesDictionary();
        if (copy.AdditionalProperties is { } existing)
            foreach (var (key, value) in existing) properties[key] = value;
        properties[PropertyName] = hint;
        copy.AdditionalProperties = properties;
        return copy;
    }

    public static VirtualModelRequestHint? ReadHint(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(PropertyName, out var value) == true
            ? value as VirtualModelRequestHint : null;

    public static ChatOptions ForPhysicalModel(ChatOptions? options, VirtualModelRequestRoute route)
    {
        var copy = options?.Clone() ?? new ChatOptions();
        var properties = new AdditionalPropertiesDictionary();
        if (copy.AdditionalProperties is { } existing)
            foreach (var (key, value) in existing) properties[key] = value;
        properties.Remove(PropertyName);
        copy.AdditionalProperties = properties;
        copy.ModelId = route.Model.Id;
        copy.Reasoning = route.Reasoning;
        return copy;
    }
}
