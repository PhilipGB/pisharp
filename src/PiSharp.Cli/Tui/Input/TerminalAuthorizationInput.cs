using System.Text;

namespace PiSharp.Cli.Tui;

internal enum TerminalLoginInputAction { CopyAuthorizationUrl, Cancel }

internal sealed class TerminalAuthorizationInput(EditorKeymap keymap, Func<TerminalInput> getInput,
    Func<TerminalScreen?> getScreen) : TextReader
{
    private readonly object _callbackGate = new();
    private TaskCompletionSource<string?>? _callbackRead;
    private string? _queuedCallback;

    public override Task<string?> ReadLineAsync() => ReadCallbackLineAsync(CancellationToken.None).AsTask();

    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        ReadCallbackLineAsync(cancellationToken);

    private async ValueTask<string?> ReadCallbackLineAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<string?> read;
        lock (_callbackGate)
        {
            if (_queuedCallback is { } queued)
            {
                _queuedCallback = null;
                return queued;
            }
            if (_callbackRead is not null)
                throw new InvalidOperationException("An MCP authorization response is already being read.");
            read = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _callbackRead = read;
        }

        try { return await read.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_callbackGate)
                if (ReferenceEquals(_callbackRead, read)) _callbackRead = null;
        }
    }

    private void SubmitCallbackLine(string line)
    {
        lock (_callbackGate)
        {
            if (_callbackRead is { } read) read.TrySetResult(line);
            else _queuedCallback = line;
        }
    }

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
        var callbackLine = new StringBuilder();
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
                break;
            }
            if (next.Mouse is { } mouse)
            {
                var result = getScreen()?.HandleMouse(mouse) ?? default;
                if (result.Copy && getScreen()?.ActiveAuthorizationUrl is not null)
                    await dispatchApplicationAction("app.message.copy");
                continue;
            }
            if (getScreen()?.ActiveAuthorizationUrl is not null && next.Text is { } pastedText)
            {
                callbackLine.Clear();
                callbackLine.Append(pastedText.Trim());
                SubmitCallbackLine(callbackLine.ToString());
                callbackLine.Clear();
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
            if (getScreen()?.ActiveAuthorizationUrl is not null && key.Key == ConsoleKey.Enter)
            {
                if (callbackLine.Length > 0)
                {
                    SubmitCallbackLine(callbackLine.ToString());
                    callbackLine.Clear();
                }
                continue;
            }
            if (getScreen()?.ActiveAuthorizationUrl is not null && key.Key == ConsoleKey.Backspace)
            {
                if (callbackLine.Length > 0) callbackLine.Length--;
                continue;
            }
            if (getScreen()?.ActiveAuthorizationUrl is not null &&
                (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0 &&
                key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                if (callbackLine.Length < 16_384) callbackLine.Append(key.KeyChar);
                continue;
            }
            if (keymap.MatchesEditorAction(key)) continue;
        }
        callbackLine.Clear();
        lock (_callbackGate) _queuedCallback = null;
    }
}
