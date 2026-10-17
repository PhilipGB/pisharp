using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Extensions;

/// <summary>Projects the durable custom-message identity through MEAI's user-message boundary.</summary>
internal static class PiSharpCustomMessageProjection
{
    private const string CustomTypeKey = "pisharp.extension.customType";
    private const string DisplayKey = "pisharp.extension.display";
    private const string SourceKey = "pisharp.extension.source";

    public static ChatMessage ToChatMessage(PiSharpCustomMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.CustomType);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Content);

        var properties = new AdditionalPropertiesDictionary
        {
            [CustomTypeKey] = message.CustomType,
            [DisplayKey] = message.Display
        };
        if (message.Source is { Length: > 0 } source) properties[SourceKey] = source;
        return new ChatMessage(ChatRole.User, message.Content) { AdditionalProperties = properties };
    }

    public static bool TryRead(ChatMessage message, out PiSharpCustomMessage customMessage)
    {
        customMessage = null!;
        var properties = message.AdditionalProperties;
        if (properties is null || !TryReadString(properties, CustomTypeKey, out var customType)) return false;

        var display = TryReadBoolean(properties, DisplayKey, out var value) && value;
        var source = TryReadString(properties, SourceKey, out var sourceValue) ? sourceValue : null;
        customMessage = new(customType, message.Text, display) { Source = source };
        return true;
    }

    private static bool TryReadString(IDictionary<string, object?> properties, string key, out string value)
    {
        value = "";
        if (!properties.TryGetValue(key, out var raw)) return false;
        if (raw is string text)
        {
            value = text;
            return true;
        }
        if (raw is JsonElement { ValueKind: JsonValueKind.String } element && element.GetString() is { } jsonText)
        {
            value = jsonText;
            return true;
        }
        return false;
    }

    private static bool TryReadBoolean(IDictionary<string, object?> properties, string key, out bool value)
    {
        value = false;
        if (!properties.TryGetValue(key, out var raw)) return false;
        if (raw is bool boolean)
        {
            value = boolean;
            return true;
        }
        if (raw is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } element)
        {
            value = element.GetBoolean();
            return true;
        }
        return false;
    }
}
