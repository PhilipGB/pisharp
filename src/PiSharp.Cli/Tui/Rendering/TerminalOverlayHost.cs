namespace PiSharp.Cli.Tui;

/// <summary>Runs reusable modal list interactions through the active terminal screen.</summary>
internal sealed class TerminalOverlayHost(TerminalInput input)
{
    public TerminalSelection<T>? Select<T>(TerminalScreen screen, string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey = null,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items",
        string? actionHint = null,
        Func<TerminalSelectionList<T>, TerminalInputEvent, bool>? handleInput = null)
    {
        Func<TerminalSelectionList<T>, TerminalInputEvent, ValueTask<bool>>? asyncHandler = handleInput is null
            ? null
            : (list, input) => ValueTask.FromResult(handleInput(list, input));
        return SelectCoreAsync(screen, title, options, selectedKey, scopedOptions, allLabel, scopedLabel,
            emptyMessage, actionHint, asyncHandler, null, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<TerminalSelection<T>?> SelectAsync<T>(TerminalScreen screen, string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey = null,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items",
        string? actionHint = null,
        Func<TerminalSelectionList<T>, TerminalInputEvent, Task<bool>>? handleInput = null,
        CancellationToken cancellationToken = default,
        Func<TerminalSelectionList<T>, Task>? onIdle = null)
    {
        Func<TerminalSelectionList<T>, TerminalInputEvent, ValueTask<bool>>? asyncHandler = handleInput is null
            ? null
            : (list, input) => new ValueTask<bool>(handleInput(list, input));
        return SelectCoreAsync(screen, title, options, selectedKey, scopedOptions, allLabel, scopedLabel,
            emptyMessage, actionHint, asyncHandler, onIdle, cancellationToken);
    }

    private async Task<TerminalSelection<T>?> SelectCoreAsync<T>(TerminalScreen screen, string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions, string allLabel, string scopedLabel,
        string emptyMessage, string? actionHint,
        Func<TerminalSelectionList<T>, TerminalInputEvent, ValueTask<bool>>? handleInput,
        Func<TerminalSelectionList<T>, Task>? onIdle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var list = new TerminalSelectionList<T>(title, options, selectedKey, scopedOptions,
            allLabel, scopedLabel, emptyMessage, actionHint);
        try
        {
            while (screen.IsActive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                screen.RefreshIfResized();
                if (onIdle is not null) await onIdle(list).ConfigureAwait(false);
                var content = list.Render(screen.TerminalWidth, screen.TerminalHeight);
                screen.SetOverlay(content);
                TerminalInputEvent next;
                if (onIdle is not null || list.Selected?.Option.SelectedLabelRenderer is not null)
                {
                    if (!input.TryRead(50, out next)) continue;
                }
                else next = input.Read();
                if (next.IsEndOfStream) return null;
                if (handleInput is not null)
                {
                    var handledTask = handleInput(list, next);
                    if (!handledTask.IsCompleted) screen.SetOverlay(null);
                    if (await handledTask.ConfigureAwait(false)) continue;
                }
                if (list.Selected is null && TerminalWheelInput.TryRouteUnhandled(screen, next)) continue;
                var mouseContentLine = next.Mouse is { } mouse
                    ? TerminalOverlayLayout.ContentLineAt(content, screen.TerminalWidth, screen.TerminalHeight,
                        mouse.Column, mouse.Row)
                    : null;
                var action = list.HandleInput(next, mouseContentLine);
                if (action is TerminalSelectionAction.Accept or TerminalSelectionAction.AcceptAsDefault)
                    return list.Selected is { } selected
                        ? selected with { SetAsDefault = action == TerminalSelectionAction.AcceptAsDefault }
                        : null;
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
