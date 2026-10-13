namespace PiSharp.Cli.Tui;

internal sealed record TerminalProgressState(string Message, double? Ratio = null, string? Detail = null);
internal readonly record struct TerminalProgressOutcome<T>(bool Cancelled, T? Value);

internal sealed class TerminalProgressDisplay(EditorKeymap keymap, Func<TerminalInput> getInput,
    Func<TerminalScreen?> getScreen, TerminalDialogs dialogs)
{
    private static readonly TimeSpan InitialProgressFrameDelay = TimeSpan.FromMilliseconds(16);

    public async Task<TerminalProgressOutcome<T>> RunProgressAsync<T>(string title, string subject,
        string cancelTitle, string cancelMessage,
        Func<CancellationToken, Action<TerminalProgressState>, Task<T>> run, Func<Task> cancelOperation,
        Func<Task>? afterCancelled = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(cancelOperation);
        if (getScreen() is not { IsActive: true } screen)
            throw new InvalidOperationException("Progress requires an active terminal screen.");

        using var mode = TerminalMode.Enter(screen);
        using var cancellation = new CancellationTokenSource();
        var input = getInput();
        var state = new TerminalProgressState("Starting…");
        var progressGate = new object();
        var progressFrameStarted = false;
        var cancelOptions = new[]
        {
            new TerminalSelectionOption<bool>("yes", true, "Yes"),
            new TerminalSelectionOption<bool>("no", false, "No")
        };
        var confirmationTitle = $"{cancelTitle}\n{cancelMessage}";
        const string confirmationFooter = "enter select • escape/ctrl+c cancel";
        void RenderProgress(TerminalProgressState progress)
        {
            var theme = screen.CurrentTheme;
            var width = screen.TerminalWidth;
            var lines = new List<string>
            {
                TerminalDialogPanels.PanelBorder(theme, width),
                TerminalDialogPanels.PadPanelLine(" " + theme.Style("accent", TerminalSafeText.Normalize(title), bold: true), width),
                TerminalDialogPanels.PadPanelLine(" " + theme.Style("text", TerminalSafeText.Normalize(subject)), width),
                "",
                TerminalDialogPanels.PadPanelLine(" " + theme.Style("muted", TerminalSafeText.Normalize(state.Message)), width)
            };
            if (state.Ratio is { } ratio)
            {
                var bounded = Math.Clamp(ratio, 0, 1);
                var filled = (int)Math.Round(bounded * 40, MidpointRounding.AwayFromZero);
                lines.Add(TerminalDialogPanels.PadPanelLine(" " + theme.Style("accent", new string('█', filled) + new string('─', 40 - filled) +
                    $" {Math.Round(bounded * 100):0}%"), width));
            }
            if (!string.IsNullOrEmpty(state.Detail))
                lines.Add(TerminalDialogPanels.PadPanelLine(" " + theme.Style("dim", TerminalSafeText.Normalize(state.Detail)), width));
            lines.Add("");
            lines.Add(TerminalDialogPanels.PadPanelLine(" " + TerminalDialogPanels.RenderKeyHint(theme, "escape/ctrl+c stop"), width));
            lines.Add(TerminalDialogPanels.PanelBorder(theme, width));
            screen.SetEditorPanel(lines, lines.Count - 4, width + 1, cursorVisible: false, bottomMargin: 2);
        }

        void Update(TerminalProgressState progress)
        {
            lock (progressGate)
            {
                state = progress;
                if (progressFrameStarted) RenderProgress(state);
            }
        }

        async Task<(bool Succeeded, T? Value, Exception? Error)> SettleAsync()
        {
            try { return (true, await run(cancellation.Token, Update).ConfigureAwait(false), null); }
            catch (Exception error) { return (false, default, error); }
        }

        var settled = SettleAsync();
        if (await Task.WhenAny(settled, Task.Delay(InitialProgressFrameDelay)).ConfigureAwait(false) != settled)
        {
            lock (progressGate)
            {
                progressFrameStarted = true;
                if (!settled.IsCompleted) RenderProgress(state);
            }
        }
        try
        {
            while (!settled.IsCompleted)
            {
                if (!input.TryRead(50, out var next))
                {
                    screen.RefreshIfResized();
                    await Task.Delay(10).ConfigureAwait(false);
                    continue;
                }
                if (TerminalWheelInput.TryRouteUnhandled(screen, next)) continue;
                if (next.Key is not { } key ||
                    !keymap.Matches("app.interrupt", key) && !keymap.Matches("app.clear", key)) continue;
                var confirmation = dialogs.ShowInlineSelectionList(confirmationTitle, cancelOptions, [""],
                    confirmationFooter, preservePanelAfterSelection: true);
                if (confirmation?.Option.Value != true)
                {
                    Update(state);
                    continue;
                }
                cancellation.Cancel();
                await cancelOperation().ConfigureAwait(false);
                _ = await settled.ConfigureAwait(false);
                if (afterCancelled is not null)
                {
                    dialogs.ShowInlineSelectionPanel(confirmationTitle, cancelOptions, [""], confirmationFooter);
                    await afterCancelled().ConfigureAwait(false);
                }
                return new(true, default);
            }

            var result = await settled.ConfigureAwait(false);
            if (!result.Succeeded) throw result.Error!;
            return new(false, result.Value);
        }
        finally
        {
            cancellation.Cancel();
        }
    }
}
