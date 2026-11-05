using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Renders the tree rows, help and status lines into terminal lines.</summary>
internal static class TerminalTreeView
{
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

        var startIndex = 0;
        var endIndex = 0;
        var selectedLineIndex = -1;
        if (!empty)
        {
            startIndex = Math.Max(0, Math.Min(state.SelectedIndex - maxVisible / 2,
                state.VisibleNodes.Count - maxVisible));
            endIndex = Math.Min(startIndex + maxVisible, state.VisibleNodes.Count);
            for (var index = startIndex; index < endIndex; index++)
            {
                var isSelected = index == state.SelectedIndex;
                lines.Add(Row(theme, state, state.VisibleNodes[index], index, isSelected, width));
                if (isSelected) selectedLineIndex = lines.Count - 1;
            }
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

    private static string SearchLine(TerminalTheme theme, string query, int width) =>
        TerminalTranscriptViewport.Clip(
            "  " + Style(theme, "muted", "Type to search:") +
            (query.Length > 0 ? " " + Style(theme, "accent", query) : ""), width);

    private static string Row(TerminalTheme theme, TerminalTreeState state, TerminalTreeFlatNode flatNode,
        int index, bool isSelected, int width)
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

        var label = flatNode.Node.Label is { } entryLabel
            ? Style(theme, "warning", $"[{entryLabel}] ")
            : "";
        var labelTimestamp = state.ShowLabelTimestamps && flatNode.Node.Label is not null &&
            flatNode.Node.LabelTimestamp is { } timestamp
                ? Style(theme, "muted", FormatLabelTimestamp(timestamp) + " ")
                : "";

        var content = Content(theme, state, flatNode, isSelected);
        var body = Style(theme, "dim", prefix) + foldMarker + pathMarker + label + labelTimestamp + content;
        var cursor = isSelected ? Style(theme, "accent", "› ") : "  ";
        var line = cursor + body;
        line = TerminalTranscriptViewport.Clip(line, width);
        return isSelected ? Style(theme, "selectedBg", line, background: true) : line;
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
