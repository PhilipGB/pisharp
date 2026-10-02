using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

internal static class SystemMessageTranscript
{
    private const string MetadataKey = "pisharp.systemMessage";

    internal static void ImportMetadata(ChatMessage message, JsonElement source)
    {
        var metadata = new JsonObject();
        foreach (var name in new[] { "sections", "toolsAdded", "toolsRemoved" })
            if (source.TryGetProperty(name, out var value)) metadata[name] = JsonNode.Parse(value.GetRawText());
        if (metadata.Count == 0) return;
        message.AdditionalProperties ??= new();
        message.AdditionalProperties[MetadataKey] = JsonSerializer.SerializeToElement(metadata);
    }

    internal static void ExportMetadata(ChatMessage message, JsonObject target)
    {
        if (Metadata(message) is not { } metadata) return;
        foreach (var property in metadata.EnumerateObject())
            target[property.Name] = JsonNode.Parse(property.Value.GetRawText());
    }

    private static JsonElement? Metadata(ChatMessage message) =>
        message.AdditionalProperties?.TryGetValue(MetadataKey, out var value) == true &&
        value is JsonElement { ValueKind: JsonValueKind.Object } metadata ? metadata : null;

    internal static JsonElement[] AddedTools(ChatMessage message) =>
        Metadata(message) is { } metadata && metadata.TryGetProperty("toolsAdded", out var tools) &&
        tools.ValueKind == JsonValueKind.Array ? tools.EnumerateArray().Select(tool => tool.Clone()).ToArray() : [];

    internal static string[] RemovedTools(ChatMessage message) =>
        Metadata(message) is { } metadata && metadata.TryGetProperty("toolsRemoved", out var tools) &&
        tools.ValueKind == JsonValueKind.Array ? tools.EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!).ToArray() : [];

    private static IEnumerable<JsonProperty> Sections(ChatMessage message) =>
        Metadata(message) is { } metadata && metadata.TryGetProperty("sections", out var sections) &&
        sections.ValueKind == JsonValueKind.Object ? sections.EnumerateObject().ToArray() : [];

    internal static string Text(ChatMessage message, bool update = false) => string.Join("\n\n",
        new[] { string.Join('\n', message.Contents.OfType<TextContent>().Select(content => content.Text)) }
            .Concat(Sections(message).Select(section => update
                ? section.Value.ValueKind == JsonValueKind.Null
                    ? $"Removed system prompt section \"{section.Name}\"."
                    : $"Updated system prompt section \"{section.Name}\":\n\n{section.Value.GetString()}"
                : section.Value.ValueKind == JsonValueKind.Null ? "" : section.Value.GetString()!))
            .Where(text => text.Length > 0));

    internal static IReadOnlyList<JsonElement> CurrentTools(IEnumerable<ChatMessage> messages)
    {
        var tools = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var message in messages.Where(message => message.Role == ChatRole.System))
        {
            foreach (var name in RemovedTools(message)) tools.Remove(name);
            foreach (var tool in AddedTools(message)) tools[tool.GetProperty("name").GetString()!] = tool;
        }
        return tools.Values.ToArray();
    }

    internal static string CurrentText(IEnumerable<ChatMessage> messages)
    {
        var content = new List<string>();
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages.Where(message => message.Role == ChatRole.System))
        {
            var text = string.Join('\n', message.Contents.OfType<TextContent>().Select(part => part.Text));
            if (text.Length > 0) content.Add(text);
            foreach (var section in Sections(message))
                if (section.Value.ValueKind == JsonValueKind.Null) sections.Remove(section.Name);
                else sections[section.Name] = section.Value.GetString()!;
        }
        return string.Join("\n\n", content.Concat(sections.Values).Where(text => text.Length > 0));
    }
}
