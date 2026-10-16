using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Cli.Tui;

/// <summary>Shows the session's parent/branch relationships and returns the selected entry.</summary>
internal sealed class TerminalTreePicker(TerminalEditor editor)
{
    private const int MaximumPreviewCells = 100;

    public ConversationNode? Show(IReadOnlyList<ConversationNode> entries, string? currentHead)
    {
        if (entries.Count == 0) return null;
        var options = ToOptions(entries, currentHead);
        return editor.ShowSelectionList("Conversation tree", options, currentHead,
            emptyMessage: "No matching conversation entries")?.Option.Value;
    }

    internal static IReadOnlyList<TerminalSelectionOption<ConversationNode>> ToOptions(
        IReadOnlyList<ConversationNode> entries, string? currentHead)
    {
        var children = entries.Where(entry => entry.ParentId is not null)
            .GroupBy(entry => entry.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var options = new List<TerminalSelectionOption<ConversationNode>>(entries.Count);
        var roots = entries.Where(entry => entry.ParentId is null).ToArray();
        var pending = new Stack<(ConversationNode Entry, string Prefix, bool IsLast)>();
        for (var index = roots.Length - 1; index >= 0; index--)
            pending.Push((roots[index], string.Empty, index == roots.Length - 1));

        while (pending.TryPop(out var row))
        {
            var entry = row.Entry;
            var isCurrent = entry.Id == currentHead;
            var childRows = children.GetValueOrDefault(entry.Id) ?? [];
            var branch = childRows.Length > 1 ? " ┬" : childRows.Length == 1 ? " │" : "";
            var marker = isCurrent ? "●" : childRows.Length == 0 ? "○" : " ";
            var label = $"{row.Prefix}{(row.IsLast ? "└─" : "├─")}{marker}{branch} {Description(entry)}";
            var description = $"{entry.Type} · {entry.Id[..Math.Min(12, entry.Id.Length)]} · {entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            var searchText = $"{entry.Id} {entry.ParentId} {entry.Type} {PreviewText(entry)}";
            options.Add(new(entry.Id, entry, label, description, searchText, isCurrent));

            var childPrefix = row.Prefix + (row.IsLast ? "  " : "│ ");
            for (var index = childRows.Length - 1; index >= 0; index--)
                pending.Push((childRows[index], childPrefix, index == childRows.Length - 1));
        }

        return options;
    }

    private static string Description(ConversationNode entry)
    {
        var preview = PreviewText(entry);
        if (preview.Length == 0) return entry.Type;
        var clipped = TerminalTranscriptViewport.Clip(TerminalSafeText.Normalize(preview), MaximumPreviewCells);
        return preview.Length > clipped.Length ? $"{clipped}…" : clipped;
    }

    private static string PreviewText(ConversationNode entry)
    {
        if (entry.Payload.ValueKind != JsonValueKind.Object) return string.Empty;
        if (entry.Type == "chat")
        {
            var message = Property(entry.Payload, "Message");
            if (message.ValueKind != JsonValueKind.Object) message = entry.Payload;
            var role = GetStringOrNull(Property(message, "Role"));
            var contents = Property(message, "Contents");
            var text = contents.ValueKind == JsonValueKind.Array
                ? string.Join(" ", contents.EnumerateArray().Select(item => GetStringOrNull(Property(item, "Text")))
                    .Where(value => !string.IsNullOrWhiteSpace(value)))
                : string.Empty;
            var toolNames = contents.ValueKind == JsonValueKind.Array
                ? contents.EnumerateArray().Select(item => GetStringOrNull(Property(item, "Name")))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
                : [];
            var body = string.IsNullOrWhiteSpace(text) && toolNames.Length > 0
                ? string.Join(", ", toolNames.Select(name => $"tool {name}"))
                : text;
            return string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(body) ? body ?? string.Empty : $"{role}: {body}";
        }

        foreach (var name in new[] { "summary", "command", "text", "name", "model", "level" })
        {
            var value = GetStringOrNull(Property(entry.Payload, name));
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return default;
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string? GetStringOrNull(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
