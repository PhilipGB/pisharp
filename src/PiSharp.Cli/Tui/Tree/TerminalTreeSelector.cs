using System.Text;
using PiSharp.Core;

namespace PiSharp.Cli.Tui;

/// <summary>Interactive session-tree navigator; mirrors pinned Pi's TreeSelectorComponent.</summary>
internal sealed class TerminalTreeSelector(Func<IReadOnlyList<ConversationNode>> freshEntries,
    Func<string, string?, Task<bool>> onSaveLabelAsync,
    Func<string?, Task> onCopyAsync,
    EditorKeymap keymap)
{
    /// <summary>Returns the selected entry, or null when the navigator was cancelled.</summary>
    public async Task<ConversationNode?> ShowAsync(TerminalScreen screen, TerminalInput input,
        string? currentLeafId, string? initialSelectedId)
    {
        var theme = screen.CurrentTheme;
        var state = new TerminalTreeState(freshEntries().ToArray(), currentLeafId);
        if (initialSelectedId is not null) state.SelectById(initialSelectedId);
        var needsRender = true;
        var lastWidth = -1;
        var lastHeight = -1;
        LabelEdit? labelEdit = null;

        using var mode = TerminalMode.Enter(screen);
        try
        {
            while (screen.IsActive)
            {
                screen.RefreshIfResized();
                var height = screen.TerminalHeight;
                var width = screen.TerminalWidth;
                if (width != lastWidth || height != lastHeight)
                {
                    lastWidth = width;
                    lastHeight = height;
                    needsRender = true;
                }
                state.MaxVisibleLines = Math.Max(5, height / 2);
                if (needsRender)
                {
                    var content = labelEdit is not { } activeEdit
                        ? TerminalTreeView.Render(state, theme, screen.TerminalWidth, height, keymap.GetDisplayKeys)
                        : RenderLabelEdit(activeEdit, theme, screen.TerminalWidth);
                    screen.SetOverlay(content);
                    needsRender = false;
                }

                if (!input.TryRead(40, out var next)) continue;
                if (next.IsEndOfStream) return null;

                if (labelEdit is { } active)
                {
                    var (edit, submitted, closed) = HandleLabelInput(active, next, keymap);
                    if (closed)
                    {
                        labelEdit = null;
                        if (submitted is { } submission)
                        {
                            var value = submission.Buffer.Trim();
                            var saved = await onSaveLabelAsync(submission.EntryId, value.Length == 0 ? null : value)
                                .ConfigureAwait(false);
                            if (saved) state.Refresh(freshEntries().ToArray());
                        }
                    }
                    else labelEdit = edit;
                    needsRender = true;
                    continue;
                }

                if (next.Text is { Length: > 0 } text)
                {
                    state.AppendSearchText(Sanitize(text));
                    needsRender = true;
                    continue;
                }

                if (next.Key is not { } key)
                {
                    needsRender = true;
                    continue;
                }

                if (keymap.Matches("tui.select.up", key)) state.MoveSelection(-1);
                else if (keymap.Matches("tui.select.down", key)) state.MoveSelection(1);
                else if (keymap.Matches("app.tree.foldOrUp", key)) state.FoldOrUp();
                else if (keymap.Matches("app.tree.unfoldOrDown", key)) state.UnfoldOrDown();
                else if (keymap.Matches("tui.editor.cursorLeft", key) || keymap.Matches("tui.select.pageUp", key))
                    state.PageUp(state.MaxVisibleLines);
                else if (keymap.Matches("tui.editor.cursorRight", key) || keymap.Matches("tui.select.pageDown", key))
                    state.PageDown(state.MaxVisibleLines);
                else if (keymap.Matches("tui.select.confirm", key))
                    return state.SelectedNode?.Node.Entry;
                else if (keymap.Matches("app.message.copy", key))
                {
                    var copyText = state.SelectedCopyText();
                    await onCopyAsync(copyText).ConfigureAwait(false);
                }
                else if (keymap.Matches("tui.select.cancel", key))
                {
                    if (!state.TryEscape()) return null;
                }
                else if (keymap.Matches("app.tree.filter.default", key)) state.SetFilterMode(TerminalTreeFilterMode.Default);
                else if (keymap.Matches("app.tree.filter.noTools", key)) state.ToggleFilterMode(TerminalTreeFilterMode.NoTools);
                else if (keymap.Matches("app.tree.filter.userOnly", key)) state.ToggleFilterMode(TerminalTreeFilterMode.UserOnly);
                else if (keymap.Matches("app.tree.filter.labeledOnly", key)) state.ToggleFilterMode(TerminalTreeFilterMode.LabeledOnly);
                else if (keymap.Matches("app.tree.filter.all", key)) state.ToggleFilterMode(TerminalTreeFilterMode.All);
                else if (keymap.Matches("app.tree.filter.cycleBackward", key)) state.CycleFilter(-1);
                else if (keymap.Matches("app.tree.filter.cycleForward", key)) state.CycleFilter(1);
                else if (keymap.Matches("tui.editor.deleteCharBackward", key)) state.SearchBackspace();
                else if (keymap.Matches("app.tree.editLabel", key))
                {
                    if (state.SelectedNode is { } node)
                        labelEdit = new LabelEdit(node.Node.Entry.Id, node.Node.Label ?? "", node.Node.Label?.Length ?? 0);
                }
                else if (keymap.Matches("app.tree.toggleLabelTimestamp", key)) state.ToggleLabelTimestamps();
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    state.AppendSearchText(key.KeyChar.ToString());
                needsRender = true;
            }
            return null;
        }
        finally
        {
            if (screen.IsActive) screen.SetOverlay(null);
        }
    }

    private static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var value in text)
            builder.Append(char.IsControl(value) ? ' ' : value);
        return builder.ToString();
    }

    private static IReadOnlyList<string> RenderLabelEdit(LabelEdit edit, TerminalTheme theme, int width)
    {
        width = Math.Max(12, width);
        var buffer = TerminalTreeText.Display(edit.Buffer);
        var cursor = Math.Clamp(edit.Cursor, 0, buffer.Length);
        var before = buffer[..cursor];
        var at = cursor < buffer.Length ? buffer[cursor].ToString() : " ";
        var after = cursor < buffer.Length ? buffer[(cursor + 1)..] : "";
        var lines = new List<string>
        {
            theme.Fg("border") + new string('─', width) + "\u001b[0m",
            "  " + theme.Style("muted", "Label (empty to remove):"),
            "  " + before + "\u001b[7m" + at + "\u001b[27m" + after,
            "  " + theme.Style("muted", "Enter save · Escape cancel"),
            theme.Fg("border") + new string('─', width) + "\u001b[0m"
        };
        return lines.Select(line => TerminalTranscriptViewport.Clip(line, width)).ToList();
    }

    private static (LabelEdit Edit, (string EntryId, string Buffer)? Submitted, bool Closed) HandleLabelInput(
        LabelEdit edit, TerminalInputEvent input, EditorKeymap keymap)
    {
        if (input.Text is { Length: > 0 } text)
        {
            var sanitized = Sanitize(text);
            var builder = new StringBuilder(edit.Buffer).Insert(Math.Min(edit.Cursor, edit.Buffer.Length), sanitized);
            return (edit with { Buffer = builder.ToString(), Cursor = edit.Cursor + sanitized.Length }, null, false);
        }
        if (input.Key is not { } key) return (edit, null, false);
        if (keymap.Matches("tui.select.confirm", key))
            return (edit, (edit.EntryId, edit.Buffer), true);
        if (keymap.Matches("tui.select.cancel", key))
            return (edit, null, true);
        if (key.Key == ConsoleKey.Backspace)
        {
            if (edit.Cursor > 0)
                return (edit with { Buffer = edit.Buffer.Remove(edit.Cursor - 1, 1), Cursor = edit.Cursor - 1 }, null, false);
            return (edit, null, false);
        }
        if (key.Key == ConsoleKey.Delete)
        {
            if (edit.Cursor < edit.Buffer.Length)
                return (edit with { Buffer = edit.Buffer.Remove(edit.Cursor, 1) }, null, false);
            return (edit, null, false);
        }
        if (key.Key == ConsoleKey.LeftArrow)
            return (edit with { Cursor = Math.Max(0, edit.Cursor - 1) }, null, false);
        if (key.Key == ConsoleKey.RightArrow)
            return (edit with { Cursor = Math.Min(edit.Buffer.Length, edit.Cursor + 1) }, null, false);
        if (key.Key == ConsoleKey.Home) return (edit with { Cursor = 0 }, null, false);
        if (key.Key == ConsoleKey.End) return (edit with { Cursor = edit.Buffer.Length }, null, false);
        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
        {
            var builder = new StringBuilder(edit.Buffer).Insert(Math.Min(edit.Cursor, edit.Buffer.Length), key.KeyChar);
            return (edit with { Buffer = builder.ToString(), Cursor = edit.Cursor + 1 }, null, false);
        }
        return (edit, null, false);
    }

    private readonly record struct LabelEdit(string EntryId, string Buffer, int Cursor);
}
