using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Reads metadata consistently before and after native session serialization.</summary>
internal static class ChatMessageProperties
{
    public static ChatMessage WithProviderIdentity(ChatMessage message, string? provider, string model,
        string api, string? thinkingLevel)
    {
        var copy = message.Clone();
        copy.AdditionalProperties = message.AdditionalProperties?.Clone() ?? new();
        copy.AdditionalProperties["pisharp.provider"] = provider;
        copy.AdditionalProperties["pisharp.model"] = model;
        copy.AdditionalProperties["pisharp.api"] = api;
        copy.AdditionalProperties["pisharp.thinkingLevel"] = thinkingLevel;
        return copy;
    }

    // MAF annotates replayed messages with the component that supplied them for this run.
    // That typed, transient annotation is separate from application-owned message metadata.
    public static ChatMessage WithoutRequestAttribution(ChatMessage message)
    {
        if (message.AdditionalProperties?.TryGetValue(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey,
            out var value) != true || !IsFrameworkAttribution(value)) return message;
        var copy = message.Clone();
        copy.AdditionalProperties = message.AdditionalProperties.Clone();
        copy.AdditionalProperties.Remove(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey);
        if (copy.AdditionalProperties.Count == 0) copy.AdditionalProperties = null;
        return copy;
    }

    private static bool IsFrameworkAttribution(object? value) =>
        value is AgentRequestMessageSourceAttribution ||
        value is JsonElement { ValueKind: JsonValueKind.Object } json &&
        json.TryGetProperty("SourceId", out var id) && id.ValueKind == JsonValueKind.String &&
        id.GetString() == typeof(InMemoryChatHistoryProvider).FullName &&
        json.TryGetProperty("SourceType", out var source) && source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty("Value", out var kind) && kind.ValueKind == JsonValueKind.String &&
        kind.GetString() == "ChatHistory";

    public static string? String(IDictionary<string, object?>? properties, string name) =>
        properties?.TryGetValue(name, out var value) == true ? value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        } : null;
}
