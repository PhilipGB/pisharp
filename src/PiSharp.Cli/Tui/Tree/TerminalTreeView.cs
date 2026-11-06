using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Renders the tree rows, help and status lines into terminal lines.</summary>
internal static class TerminalTreeView
{
    private const int TreeGutterWidth = 2;
    private const int MinimumVisibleAnchorContentWidth = 4;
    private const int MaximumVisibleAnchorContentWidth = 20;
    private const int MinimumAnchorContextWidth = 2;
    private const int MaximumAnchorContextWidth = 12;

    private readonly record struct HorizontalRow(string Gutter, string Body, int AnchorCol, int BodyWidth,
        bool IsSelected);

    public static IReadOnlyList<string> Render(TerminalTreeState state, TerminalTheme theme, int width, int height,
        Func<string, string?> displayKeys) =>
        RenderCore(state, theme, width, height, displayKeys);

    private static IReadOnlyList<string> RenderCore(TerminalTreeState state, TerminalTheme theme, int width,
        int height, Func<string, string?> displayKeys)
    {
        width = Math.Max(12, width);
        var maxVisible = state.MaxVisibleLines;
        var empty = state.VisibleNodes.Count == 0;

        var lines = new List<string>
        {
            Border(theme, width),
            Style(theme, "muted", "  ") + Style(theme, "accent", "Session Tree", bold: true)
        };
        lines.Add(HelpLine(theme, displayKeys, width));
        lines.Add(SearchLine(theme, state.SearchQuery, width));
        lines.Add(Border(theme, width));

        if (!empty)
        {
            var startIndex = Math.Max(0, Math.Min(state.SelectedIndex - maxVisible / 2,
                state.VisibleNodes.Count - maxVisible));
            var endIndex = Math.Min(startIndex + maxVisible, state.VisibleNodes.Count);
            var rows = new List<HorizontalRow>(endIndex - startIndex);
            for (var index = startIndex; index < endIndex; index++)
                rows.Add(Row(theme, state, state.VisibleNodes[index], index == state.SelectedIndex));
            lines.AddRange(RenderHorizontalViewport(rows, theme, width));
        }
        else
        {
            lines.Add(Style(theme, "muted", "  No entries found"));
            lines.Add(Style(theme, "muted", $"  (0/0){StatusLabels(state)}"));
        }

        lines.Add(Border(theme, width));
        lines.Add(Style(theme, "muted",
            $"  ({(empty ? 0 : state.SelectedIndex + 1)}/{state.VisibleNodes.Count}){StatusLabels(state)}"));

        return lines.Take(Math.Max(3, height)).ToList();
    }

    private static string HelpLine(TerminalTheme theme, Func<string, string?> displayKeys, int width)
    {
        var items = new List<string?>
        {
            MoveHint(displayKeys),
            CombinedHint(displayKeys, ["tui.editor.cursorLeft", "tui.editor.cursorRight"], "page"),
            CombinedHint(displayKeys, ["app.tree.foldOrUp", "app.tree.unfoldOrDown"], "branch"),
            KeyHint(displayKeys, "app.message.copy", "copy"),
            KeyHint(displayKeys, "app.tree.editLabel", "label"),
            KeyHint(displayKeys, "app.tree.toggleLabelTimestamp", "label time"),
            CombinedHint(displayKeys,
            [
                "app.tree.filter.default", "app.tree.filter.noTools", "app.tree.filter.userOnly",
                "app.tree.filter.labeledOnly", "app.tree.filter.all"
            ], "filters"),
            CombinedHint(displayKeys, ["app.tree.filter.cycleForward", "app.tree.filter.cycleBackward"], "cycle")
        };
        var text = "  " + string.Join(" · ", items.Where(item => !string.IsNullOrEmpty(item)));
        return TerminalTranscriptViewport.Clip(Style(theme, "muted", text), width);
    }

    private static string? MoveHint(Func<string, string?> displayKeys)
    {
        var up = FirstKey(displayKeys, "tui.select.up");
        var down = FirstKey(displayKeys, "tui.select.down");
        var keys = string.Join("/", new[] { up, down }.Where(value => !string.IsNullOrEmpty(value)));
        return keys.Length == 0 ? null : $"{keys} move";
    }

    private static string? CombinedHint(Func<string, string?> displayKeys, IReadOnlyList<string> actions, string label)
    {
        var keys = actions.Select(action => FirstKey(displayKeys, action))
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();
        if (keys.Length == 0) return null;
        return $"{Compact(keys)} {label}";
    }

    private static string? KeyHint(Func<string, string?> displayKeys, string action, string label)
    {
        var keys = FirstKey(displayKeys, action);
        return string.IsNullOrEmpty(keys) ? null : $"{Compact([keys])} {label}";
    }

    private static string? FirstKey(Func<string, string?> displayKeys, string action)
    {
        var display = displayKeys(action);
        if (string.IsNullOrEmpty(display)) return null;
        return display.Split('/')[0].Trim();
    }

    private static string Compact(IReadOnlyList<string?> keys)
    {
        if (keys.Count == 1) return keys[0]!;
        static (string Prefix, string Suffix) Split(string? key)
        {
            var separator = key?.LastIndexOf('+') ?? -1;
            var value = key ?? "";
            return separator < 0 ? ("", value) : (value[..(separator + 1)], value[(separator + 1)..]);
        }

        var parts = keys.Select(Split).ToArray();
        var prefix = parts[0].Prefix;
        if (prefix.Length > 0 && parts.All(part => part.Prefix == prefix))
            return $"{prefix}{string.Join("/", parts.Select(part => part.Suffix))}";
        return string.Join("/", keys);
    }

    private static string SearchLine(TerminalTheme theme, string query, int width)
    {
        var safeQuery = TerminalTreeText.Line(query);
        return TerminalTranscriptViewport.Clip(
            "  " + Style(theme, "muted", "Type to search:") +
            (safeQuery.Length > 0 ? " " + Style(theme, "accent", safeQuery) : ""), width);
    }

    private static HorizontalRow Row(TerminalTheme theme, TerminalTreeState state, TerminalTreeFlatNode flatNode,
        bool isSelected)
    {
        var entry = flatNode.Node.Entry;
        var multipleRoots = state.HasMultipleRoots;
        var displayIndent = multipleRoots ? Math.Max(0, flatNode.Indent - 1) : flatNode.Indent;
        var isFolded = state.IsFolded(entry.Id);
        var prefixBuilder = new List<char>(displayIndent * 3);
        var connectorDisplayed = flatNode.ShowConnector && !flatNode.IsVirtualRootChild;
        var connectorPosition = connectorDisplayed ? displayIndent - 1 : -1;
        for (var column = 0; column < displayIndent * 3; column++)
        {
            var level = column / 3;
            var position = column % 3;
            var gutter = flatNode.Gutters.Cast<TreeGutter?>().FirstOrDefault(candidate => candidate?.Position == level);
            if (gutter is { } levelGutter)
            {
                prefixBuilder.Add(position == 0 ? levelGutter.Show ? '│' : ' ' : ' ');
                continue;
            }
            if (connectorDisplayed && level == connectorPosition)
            {
                if (position == 0) prefixBuilder.Add(flatNode.IsLast ? '└' : '├');
                else if (position == 1)
                {
                    var foldable = state.IsFoldable(entry.Id);
                    prefixBuilder.Add(isFolded ? '⊞' : foldable ? '⊟' : '─');
                }
                else prefixBuilder.Add(' ');
                continue;
            }
            prefixBuilder.Add(' ');
        }

        var prefix = new string(prefixBuilder.ToArray());
        var showsFoldInConnector = connectorDisplayed;
        var foldMarker = isFolded && !showsFoldInConnector ? Style(theme, "accent", "⊞ ") : "";
        var pathMarker = state.IsOnActivePath(entry.Id) ? Style(theme, "accent", "• ") : "";

        var safeLabel = flatNode.Node.Label is { } entryLabel ? TerminalTreeText.Line(entryLabel) : "";
        var label = safeLabel.Length > 0
            ? Style(theme, "warning", $"[{safeLabel}] ")
            : "";
        var labelTimestamp = state.ShowLabelTimestamps && flatNode.Node.Label is not null &&
            flatNode.Node.LabelTimestamp is { } timestamp
                ? Style(theme, "muted", FormatLabelTimestamp(timestamp) + " ")
                : "";

        var content = Content(theme, state, flatNode, isSelected);
        var anchor = Style(theme, "dim", prefix) + foldMarker + pathMarker;
        var body = anchor + label + labelTimestamp + content;
        var cursor = isSelected ? Style(theme, "accent", "› ") : "  ";
        return new HorizontalRow(cursor, body, TerminalTextLayout.Width(anchor),
            TerminalTextLayout.Width(body), isSelected);
    }

    /// <summary>Keeps the navigation gutter fixed and pans row bodies when the selected anchor is off-screen.</summary>
    private static IReadOnlyList<string> RenderHorizontalViewport(IReadOnlyList<HorizontalRow> rows,
        TerminalTheme theme, int width)
    {
        var viewportWidth = Math.Max(0, width - TreeGutterWidth);
        var maxBodyWidth = rows.Count == 0 ? 0 : rows.Max(row => row.BodyWidth);
        var maxHorizontalScroll = Math.Max(0, maxBodyWidth - viewportWidth);
        var selectedRow = rows.FirstOrDefault(row => row.IsSelected);

        var horizontalScroll = 0;
        if (selectedRow.IsSelected && maxHorizontalScroll > 0)
        {
            var minVisibleAnchorContentWidth = Math.Min(MaximumVisibleAnchorContentWidth,
                Math.Max(MinimumVisibleAnchorContentWidth, viewportWidth / 3));
            if (selectedRow.AnchorCol > viewportWidth - minVisibleAnchorContentWidth)
            {
                var anchorContextWidth = Math.Min(MaximumAnchorContextWidth,
                    Math.Max(MinimumAnchorContextWidth, viewportWidth / 4));
                horizontalScroll = Math.Min(maxHorizontalScroll, selectedRow.AnchorCol - anchorContextWidth);
            }
        }

        var rendered = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var line = horizontalScroll > 0
                ? row.Gutter + TerminalTextLayout.SliceStyledCells(row.Body, horizontalScroll,
                    horizontalScroll + viewportWidth) + "\u001b[0m"
                : row.Gutter + row.Body;
            line = TerminalTranscriptViewport.Clip(line, width);
            rendered.Add(row.IsSelected ? Style(theme, "selectedBg", line, background: true) : line);
        }
        return rendered;
    }

    private static string Content(TerminalTheme theme, TerminalTreeState state, TerminalTreeFlatNode flatNode,
        bool isSelected)
    {
        var info = state.Decode(flatNode.Node.Entry.Id);
        return info.DisplayKind switch
        {
            "user" => Style(theme, "accent", "user: ", bold: isSelected) + info.Text,
            "assistant" => Style(theme, "success", "assistant: ", bold: isSelected) + Placeholder(theme, info),
            "toolResult" => Style(theme, "muted", info.Text, bold: isSelected),
            "bashExecution" => Style(theme, "dim", $"[bash]: {info.Text}", bold: isSelected),
            "custom_message" => Style(theme, "customMessageLabel", info.Prefix, bold: isSelected) + info.Text,
            "compaction" => Style(theme, "borderAccent", info.Prefix + info.Text, bold: isSelected),
            "branch_summary" => Style(theme, "warning", "[branch summary]: ", bold: isSelected) + info.Text,
            "system" => Style(theme, "dim", $"[system] {info.Text}".TrimEnd(), bold: isSelected),
            "model_change" or "thinking_level_change" or "label" or "session_info" or "context_edit" or
                "extension_state" => Style(theme, "dim", $"[{info.Prefix}{info.Text}]", bold: isSelected),
            _ => Style(theme, "dim", $"{info.Prefix}{info.Text}".TrimEnd(), bold: isSelected)
        };
    }

    private static string Placeholder(TerminalTheme theme, TerminalTreeEntryInfo info) =>
        info.AssistantPlaceholder switch
        {
            "aborted" => Style(theme, "muted", "(aborted)"),
            "error" => Style(theme, "error", info.Text),
            "no-content" => Style(theme, "muted", "(no content)"),
            _ => info.Text
        };

    private static string StatusLabels(TerminalTreeState state)
    {
        var labels = state.FilterMode switch
        {
            TerminalTreeFilterMode.NoTools => " [no-tools]",
            TerminalTreeFilterMode.UserOnly => " [user]",
            TerminalTreeFilterMode.LabeledOnly => " [labeled]",
            TerminalTreeFilterMode.All => " [all]",
            _ => ""
        };
        if (state.ShowLabelTimestamps) labels += " [+label time]";
        return labels;
    }

    private static string Border(TerminalTheme theme, int width) =>
        theme.Fg("border") + new string('─', width) + "\u001b[0m";

    private static string Style(TerminalTheme theme, string token, string text, bool bold = false,
        bool background = false) =>
        theme.Style(token, text, background: background, bold: bold);

    internal static string FormatLabelTimestamp(string timestamp, DateTimeOffset? now = null)
    {
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date))
            return timestamp;
        date = date.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime();
        var time = $"{date.Hour:00}:{date.Minute:00}";
        if (date.Year == today.Year && date.Month == today.Month && date.Day == today.Day)
            return time;
        if (date.Year == today.Year)
            return $"{date.Month}/{date.Day} {time}";
        return $"{date.Year % 100:00}/{date.Month}/{date.Day} {time}";
    }
}
