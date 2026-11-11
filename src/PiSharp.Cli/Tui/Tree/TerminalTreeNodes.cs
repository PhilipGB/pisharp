using PiSharp.Core;

namespace PiSharp.Cli.Tui;

/// <summary>Tree navigator filter modes, mirroring pinned Pi's FilterMode.</summary>
internal enum TerminalTreeFilterMode
{
    Default,
    NoTools,
    UserOnly,
    LabeledOnly,
    All
}

/// <summary>Maps `treeFilterMode` setting values onto the filter enum (same order).</summary>
internal static class TerminalTreeFilterModes
{
    public static TerminalTreeFilterMode Parse(string? value)
    {
        var index = value is null ? -1 : Array.IndexOf(TreeFilterModeSetting.Values, value);
        return index < 0 ? TerminalTreeFilterMode.Default : (TerminalTreeFilterMode)index;
    }
}

internal readonly record struct TreeGutter(int Position, bool Show);

/// <summary>One flattened program entry with its resolved label and tree position.</summary>
internal sealed class TerminalTreeNode
{
    public required ConversationNode Entry { get; init; }
    public int AppendIndex { get; init; }
    public string? Label { get; set; }
    public string? LabelTimestamp { get; set; }
}

/// <summary>A node flattened into visible order with its render geometry.</summary>
internal sealed class TerminalTreeFlatNode
{
    public required TerminalTreeNode Node { get; init; }
    public int Indent { get; set; }
    public bool ShowConnector { get; set; }
    public bool IsLast { get; set; }
    public IReadOnlyList<TreeGutter> Gutters { get; set; } = [];
    public bool IsVirtualRootChild { get; set; }
}

/// <summary>Decoded display/filter facts for one entry.</summary>
internal sealed record TerminalTreeEntryInfo
{
    public required string DisplayKind { get; init; }
    public required string Prefix { get; init; }
    public required string Text { get; init; }
    public required string SearchableText { get; init; }
    public string? CopyText { get; init; }
    public bool IsSettingsEntry { get; init; }
    public bool IsToolResult { get; init; }
    public bool HasText { get; init; }
    public bool HasErrorStop { get; init; }
    public bool IsBookkeeping { get; init; }
    /// <summary>Set when an assistant entry's visible content is a placeholder (no text blocks).</summary>
    public string? AssistantPlaceholder { get; init; }
}
