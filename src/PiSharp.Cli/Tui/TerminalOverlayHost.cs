namespace PiSharp.Cli.Tui;

/// <summary>Runs reusable modal list interactions through the active terminal screen.</summary>
internal sealed class TerminalOverlayHost(TerminalInput input)
{
    public TerminalSelection<T>? Select<T>(TerminalScreen screen, string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey = null,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items")
    {
        ArgumentNullException.ThrowIfNull(screen);
        var list = new TerminalSelectionList<T>(title, options, selectedKey, scopedOptions,
            allLabel, scopedLabel, emptyMessage);
        try
        {
            while (screen.IsActive)
            {
                screen.RefreshIfResized();
                var content = list.Render(screen.TerminalWidth, screen.TerminalHeight);
                screen.SetOverlay(content);
                var next = input.Read();
                if (next.Key is null && next.Text is null && next.Mouse is null) return null;
                var mouseContentLine = next.Mouse is { } mouse
                    ? TerminalOverlayLayout.ContentLineAt(content, screen.TerminalWidth, screen.TerminalHeight,
                        mouse.Column, mouse.Row)
                    : null;
                var action = list.HandleInput(next, mouseContentLine);
                if (action == TerminalSelectionAction.Accept) return list.Selected;
                if (action == TerminalSelectionAction.Cancel) return null;
            }
            return null;
        }
        finally
        {
            if (screen.IsActive) screen.SetOverlay(null);
        }
    }
}
