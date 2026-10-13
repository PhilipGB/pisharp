namespace PiSharp.Cli.Tui;

internal enum TerminalLoginInputAction { CopyAuthorizationUrl, Cancel }

internal sealed class TerminalAuthorizationInput(EditorKeymap keymap, Func<TerminalInput> getInput,
    Func<TerminalScreen?> getScreen)
{
    public TerminalLoginInputAction? TryReadLoginAction()
    {
        if (!getInput().TryRead(50, out var input)) return null;
        if (input.Mouse is { } mouse)
        {
            var result = getScreen()?.HandleMouse(mouse) ?? default;
            return result.Copy ? TerminalLoginInputAction.CopyAuthorizationUrl : null;
        }
        if (input.Key is not { } key) return null;
        if (keymap.Matches("tui.select.cancel", key) || keymap.Matches("app.interrupt", key) ||
            keymap.Matches("app.clear", key))
            return TerminalLoginInputAction.Cancel;
        if (keymap.Matches("app.message.copy", key)) return TerminalLoginInputAction.CopyAuthorizationUrl;
        return null;
    }

    public async Task MonitorMcpAuthorizationAsync(Task operation,
        Func<string, Task> dispatchApplicationAction, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(dispatchApplicationAction);
        ArgumentNullException.ThrowIfNull(cancel);
        var input = getInput();
        using var mode = TerminalMode.Enter(getScreen());
        while (!operation.IsCompleted)
        {
            if (!input.TryRead(40, out var next))
            {
                getScreen()?.RefreshIfResized();
                continue;
            }
            if (next.IsEndOfStream)
            {
                cancel();
                return;
            }
            if (next.Mouse is { } mouse)
            {
                var result = getScreen()?.HandleMouse(mouse) ?? default;
                if (result.Copy && getScreen()?.ActiveAuthorizationUrl is not null)
                    await dispatchApplicationAction("app.message.copy");
                continue;
            }
            if (next.Key is not { } key) continue;
            if (keymap.Matches("tui.select.cancel", key) || keymap.Matches("app.interrupt", key) ||
                keymap.Matches("app.clear", key))
            {
                cancel();
                continue;
            }
            if (keymap.Matches("app.message.copy", key))
            {
                if (getScreen()?.ActiveAuthorizationUrl is not null)
                    await dispatchApplicationAction("app.message.copy");
                continue;
            }
            if (keymap.MatchesEditorAction(key)) continue;
        }
    }
}
