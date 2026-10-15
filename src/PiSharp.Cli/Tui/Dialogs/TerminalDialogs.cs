namespace PiSharp.Cli.Tui;

internal sealed class TerminalDialogs(EditorKeymap keymap, Func<TerminalInput> getInput,
    Func<TerminalScreen?> getScreen, Func<CancellationToken, Task<string?>> readRedirectedLine)
{
    public TerminalSelection<T>? ShowSelectionList<T>(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey = null,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items",
        string? actionHint = null,
        Func<TerminalSelectionList<T>, TerminalInputEvent, bool>? handleInput = null)
    {
        if (getScreen() is not { IsActive: true } screen) return null;
        var input = getInput();
        using var mode = TerminalMode.Enter(screen);
        return new TerminalOverlayHost(input).Select(screen, title, options, selectedKey, scopedOptions,
            allLabel, scopedLabel, emptyMessage, actionHint, handleInput);
    }

    public TerminalSelection<T>? ShowModelSelectionList<T>(
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey, string hint,
        Func<T, string> modelName, IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        Func<CancellationToken, Task<TerminalSelectionRefresh<T>>>? refreshModels = null,
        CancellationToken cancellationToken = default)
    {
        if (getScreen() is not { IsActive: true } screen) return null;
        var input = getInput();
        using var mode = TerminalMode.Enter(screen);
        var list = new TerminalSelectionList<T>("Select model", options, selectedKey, scopedOptions,
            allLabel: "all", scopedLabel: "scoped", emptyMessage: "No matching models");
        var preservePanel = false;
        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var refreshStatus = refreshModels is null ? "Model catalogs refreshed." : "Refreshing model catalogs…";
        var refreshSuccess = refreshModels is null;
        var needsRender = true;
        Task<TerminalSelectionRefresh<T>>? refreshTask = null;
        try
        {
            var initialContent = list.RenderModelPicker(screen.TerminalWidth, screen.CurrentTheme, hint,
                refreshStatus, refreshSuccess, modelName, out var initialCursorRow, out var initialCursorColumn);
            screen.SetEditorPanel(initialContent, initialCursorRow, initialCursorColumn,
                cursorVisible: false, bottomMargin: 2);
            needsRender = false;
            if (refreshModels is not null) refreshTask = refreshModels(refreshCancellation.Token);

            while (screen.IsActive)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                screen.RefreshIfResized();
                if (refreshTask is { IsCompleted: true })
                {
                    if (refreshTask.IsCompletedSuccessfully)
                    {
                        var refreshed = refreshTask.Result;
                        list.ReplaceOptions(refreshed.Options, refreshed.ScopedOptions);
                        refreshStatus = refreshed.Status;
                        refreshSuccess = refreshed.IsSuccess;
                    }
                    else if (refreshTask.IsCanceled)
                    {
                        if (cancellationToken.IsCancellationRequested) return null;
                        refreshStatus = "Model refresh timed out; showing cached models.";
                        refreshSuccess = false;
                    }
                    else
                    {
                        refreshStatus = "Could not refresh model catalogs; showing cached models.";
                        refreshSuccess = false;
                    }
                    refreshTask = null;
                    needsRender = true;
                }

                if (needsRender)
                {
                    var content = list.RenderModelPicker(screen.TerminalWidth, screen.CurrentTheme, hint,
                        refreshStatus, refreshSuccess, modelName, out var cursorRow, out var cursorColumn);
                    screen.SetEditorPanel(content, cursorRow, cursorColumn, cursorVisible: false, bottomMargin: 2);
                    needsRender = false;
                }

                if (!input.TryRead(40, out var next)) continue;
                if (list.Selected is null && TerminalWheelInput.TryRouteUnhandled(screen, next)) continue;
                TerminalSelectionAction action;
                if (TerminalInput.TryGetText(next, out _))
                {
                    var text = input.ReadAvailableText(next, out var following);
                    action = list.HandleInput(new(null, text));
                    if (following is not null) input.PushBack(following);
                }
                else action = list.HandleInput(next);
                if (action is TerminalSelectionAction.Accept or TerminalSelectionAction.AcceptAsDefault)
                {
                    preservePanel = true;
                    refreshCancellation.Cancel();
                    return list.Selected is { } selected
                        ? selected with { SetAsDefault = action == TerminalSelectionAction.AcceptAsDefault }
                        : null;
                }
                if (action == TerminalSelectionAction.Cancel) return null;
                if (next.IsEndOfStream) return null;
                needsRender = true;
            }
            return null;
        }
        finally
        {
            refreshCancellation.Cancel();
            if (!preservePanel) screen.SetEditorPanel(null);
        }
    }

    public TerminalSelection<T>? ShowInlineSelectionList<T>(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, IReadOnlyList<string>? header = null,
        string footer = "↑↓ move • enter select • escape/ctrl+c close", string? selectedKey = null,
        bool preservePanelAfterSelection = false, int optionIndent = 0, int bottomMargin = 2,
        int bottomSpacerLines = 0, bool showCurrentMarker = false,
        CancellationToken cancellationToken = default, bool extensionDialog = false, string headerStyle = "dim")
    {
        if (getScreen() is not { IsActive: true } screen || options.Count == 0) return null;
        var input = getInput();
        var selectedIndex = selectedKey is null ? 0 : Math.Max(0,
            Array.FindIndex(options.ToArray(), option => option.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase)));
        using var mode = TerminalMode.Enter(screen);
        var preservePanel = false;
        var needsRender = true;
        var renderedWidth = -1;
        var renderedHeight = -1;
        try
        {
            while (screen.IsActive)
            {
                cancellationToken.ThrowIfCancellationRequested();
                screen.RefreshIfResized();
                if (needsRender || renderedWidth != screen.TerminalWidth || renderedHeight != screen.TerminalHeight)
                {
                    var panel = extensionDialog
                        ? TerminalDialogPanels.RenderExtensionSelectorPanel(screen, title, options, selectedIndex)
                        : TerminalDialogPanels.RenderInlinePanel(screen.CurrentTheme, screen.TerminalWidth, title, options, selectedIndex, header, footer,
                            optionIndent, bottomSpacerLines, showCurrentMarker, headerStyle);
                    if (extensionDialog)
                        screen.SetStartupPanel(panel, panel.Count - 1, screen.TerminalWidth + 1, cursorVisible: false);
                    else
                        screen.SetEditorPanel(panel, panel.Count - 2, screen.TerminalWidth + 1, cursorVisible: false,
                            bottomMargin);
                    if (extensionDialog) screen.RenderInitial();
                    renderedWidth = screen.TerminalWidth;
                    renderedHeight = screen.TerminalHeight;
                    needsRender = false;
                }
                if (!input.TryRead(40, out var next)) continue;
                while (true)
                {
                    if (next.IsEndOfStream) return null;
                    if (TerminalWheelInput.TryRouteUnhandled(screen, next))
                    {
                        if (!extensionDialog || !input.TryRead(0, out next)) break;
                        continue;
                    }
                    if (next.Key is { } key)
                    {
                        if (key.Key == ConsoleKey.Escape || keymap.Matches("app.interrupt", key) ||
                            keymap.Matches("app.clear", key)) return null;
                        if (key.Key == ConsoleKey.Enter)
                        {
                            preservePanel = preservePanelAfterSelection;
                            return new(options[selectedIndex], IsScoped: false);
                        }
                        if (key.Key == ConsoleKey.UpArrow) selectedIndex = (selectedIndex + options.Count - 1) % options.Count;
                        else if (key.Key == ConsoleKey.DownArrow) selectedIndex = (selectedIndex + 1) % options.Count;
                        else if (key.Key == ConsoleKey.Home) selectedIndex = 0;
                        else if (key.Key == ConsoleKey.End) selectedIndex = options.Count - 1;
                        else if (key.Key == ConsoleKey.PageUp) selectedIndex = Math.Max(0, selectedIndex - 10);
                        else if (key.Key == ConsoleKey.PageDown) selectedIndex = Math.Min(options.Count - 1, selectedIndex + 10);
                        needsRender = true;
                    }

                    if (!extensionDialog || !input.TryRead(0, out next)) break;
                }
            }
            return null;
        }
        finally
        {
            if (!preservePanel)
            {
                if (extensionDialog) screen.SetStartupPanel([]);
                else screen.SetEditorPanel(null);
            }
        }
    }

    public void ShowInlineSelectionPanel<T>(string title, IReadOnlyList<TerminalSelectionOption<T>> options,
        IReadOnlyList<string>? header = null, string footer = "↑↓ move • enter select • escape/ctrl+c close",
        int selectedIndex = 0)
    {
        if (getScreen() is not { IsActive: true } screen || options.Count == 0) return;
        var panel = TerminalDialogPanels.RenderInlinePanel(screen.CurrentTheme, screen.TerminalWidth, title, options,
            Math.Clamp(selectedIndex, 0, options.Count - 1), header, footer);
        screen.SetEditorPanel(panel, panel.Count - 2, screen.TerminalWidth + 1, cursorVisible: false, bottomMargin: 2);
    }

    public async Task<IReadOnlyList<string>?> PromptSequenceAsync(string title,
        IReadOnlyList<(string Message, string? Placeholder)> prompts, bool preservePanelAfterSubmit = false,
        bool preservePanelOnCancel = false, CancellationToken cancellationToken = default,
        bool extensionDialog = false, Func<string, Task>? dispatchApplicationAction = null)
    {
        if (prompts.Count == 0) return [];
        if (getScreen() is not { IsActive: true } screen)
        {
            var redirectedValues = new List<string>(prompts.Count);
            foreach (var prompt in prompts)
            {
                Console.Error.WriteLine(prompt.Message);
                var value = await readRedirectedLine(cancellationToken).ConfigureAwait(false);
                if (value is null) return null;
                redirectedValues.Add(value);
            }
            return redirectedValues;
        }

        var values = new List<string>(prompts.Count);
        var input = getInput();
        var preservePanel = false;
        using var mode = TerminalMode.Enter(screen);
        try
        {
            var titleTheme = screen.CurrentTheme;
            var titleBorder = titleTheme.Fg("border") + new string('─', Math.Max(1, screen.TerminalWidth)) + "\u001b[0m";
            var titlePanel = new[]
            {
                titleBorder,
                TerminalDialogPanels.PadPanelLine(" " + titleTheme.Style("accent", TerminalSafeText.Normalize(title), bold: true),
                    screen.TerminalWidth),
                titleBorder
            };
            if (!extensionDialog) screen.SetEditorPanel(titlePanel, cursorVisible: false, bottomMargin: 2);
            for (var promptIndex = 0; promptIndex < prompts.Count; promptIndex++)
            {
                var promptBuffer = new EditorBuffer(keymap);
                var needsRender = true;
                var renderedWidth = -1;
                var renderedHeight = -1;
                while (screen.IsActive)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    screen.RefreshIfResized();
                    if (renderedWidth != screen.TerminalWidth || renderedHeight != screen.TerminalHeight)
                        needsRender = true;
                    if (needsRender)
                    {
                        int cursorRow;
                        int cursorColumn;
                        var panel = extensionDialog
                            ? TerminalDialogPanels.RenderExtensionInputPanel(screen, title, promptBuffer.Text, promptBuffer.Cursor,
                                out cursorRow, out cursorColumn)
                            : TerminalDialogPanels.RenderPromptPanel(screen, title, prompts, values, promptIndex, promptBuffer.Text,
                                promptBuffer.Cursor, out cursorRow, out cursorColumn);
                        if (extensionDialog)
                            screen.SetStartupPanel(panel, cursorRow, cursorColumn, cursorVisible: false);
                        else
                            screen.SetEditorPanel(panel, cursorRow, cursorColumn, cursorVisible: false, bottomMargin: 2);
                        if (extensionDialog) screen.RenderInitial();
                        renderedWidth = screen.TerminalWidth;
                        renderedHeight = screen.TerminalHeight;
                        needsRender = false;
                    }

                    if (!input.TryRead(40, out var next)) continue;
                    if (TerminalWheelInput.TryRouteUnhandled(screen, next)) continue;
                    if (extensionDialog)
                    {
                        var submitted = false;
                        while (true)
                        {
                            if (next.IsEndOfStream)
                            {
                                preservePanel = preservePanelOnCancel;
                                return null;
                            }
                            if (TerminalWheelInput.TryRouteUnhandled(screen, next))
                            {
                                if (!input.TryRead(0, out next)) break;
                                continue;
                            }
                            if (next.Key is { } extensionKey && (extensionKey.Key == ConsoleKey.Escape ||
                                keymap.Matches("app.interrupt", extensionKey) ||
                                keymap.Matches("app.clear", extensionKey)))
                            {
                                preservePanel = preservePanelOnCancel;
                                return null;
                            }
                            if (next.Key is { Key: ConsoleKey.Enter })
                            {
                                values.Add(promptBuffer.Text);
                                submitted = true;
                                break;
                            }
                            if (TerminalInput.TryGetText(next, out _))
                            {
                                var text = input.ReadAvailableText(next, out var following);
                                _ = promptBuffer.InsertText(text);
                                if (following is { } pending)
                                {
                                    next = pending;
                                    continue;
                                }
                            }
                            else if (next.Key is { } editKey)
                            {
                                _ = promptBuffer.Handle(editKey);
                            }

                            if (!input.TryRead(0, out next)) break;
                        }
                        if (submitted) break;
                        needsRender = true;
                        continue;
                    }
                    if (next.Mouse is { } mouse)
                    {
                        var mouseResult = getScreen()?.HandleMouse(mouse) ?? default;
                        if (mouseResult.Copy && getScreen()?.ActiveAuthorizationUrl is not null &&
                            dispatchApplicationAction is not null)
                            await dispatchApplicationAction("app.message.copy");
                        needsRender = true;
                        continue;
                    }
                    if (next.IsEndOfStream)
                    {
                        preservePanel = preservePanelOnCancel;
                        return null;
                    }
                    if (next.Key is { } key && (key.Key == ConsoleKey.Escape ||
                        keymap.Matches("app.interrupt", key) || keymap.Matches("app.clear", key)))
                    {
                        preservePanel = preservePanelOnCancel;
                        return null;
                    }
                    if (dispatchApplicationAction is not null && next.Key is { } copyKey &&
                        keymap.Matches("app.message.copy", copyKey) &&
                        getScreen()?.ActiveAuthorizationUrl is not null)
                    {
                        await dispatchApplicationAction("app.message.copy");
                        needsRender = true;
                        continue;
                    }
                    if (next.Key is { } submit && submit.Key == ConsoleKey.Enter)
                    {
                        values.Add(promptBuffer.Text);
                        var submittedPanel = TerminalDialogPanels.RenderPromptPanel(screen, title, prompts, values, promptIndex,
                            promptBuffer.Text, promptBuffer.Cursor, out var submittedCursorRow,
                            out var submittedCursorColumn, submittedPrompt: true);
                        screen.SetEditorPanel(submittedPanel, submittedCursorRow, submittedCursorColumn,
                            cursorVisible: false, bottomMargin: 2, renderImmediately: true);
                        break;
                    }

                    if (TerminalInput.TryGetText(next, out _))
                    {
                        var text = input.ReadAvailableText(next, out var following);
                        _ = promptBuffer.InsertText(text);
                        if (following is not null) input.PushBack(following);
                    }
                    else if (next.Key is { } editKey) _ = promptBuffer.Handle(editKey);
                    needsRender = true;
                }
                if (!screen.IsActive)
                {
                    preservePanel = preservePanelOnCancel;
                    return null;
                }
            }
            preservePanel = preservePanelAfterSubmit;
            return values;
        }
        finally
        {
            if (!preservePanel)
            {
                if (extensionDialog) screen.SetStartupPanel([]);
                else screen.SetEditorPanel(null);
            }
        }
    }

    public void ShowInlineStatus(string title, string message)
    {
        if (getScreen() is not { IsActive: true } screen) return;
        var theme = screen.CurrentTheme;
        var width = screen.TerminalWidth;
        var lines = new[]
        {
            TerminalDialogPanels.PanelBorder(theme, width),
            TerminalDialogPanels.PadPanelLine(" " + theme.Style("accent", TerminalSafeText.Normalize(title), bold: true), width),
            "",
            TerminalDialogPanels.PadPanelLine(" " + theme.Style("muted", TerminalSafeText.Normalize(message)), width),
            TerminalDialogPanels.PanelBorder(theme, width)
        };
        screen.SetEditorPanel(lines, lines.Length - 2, width + 1, cursorVisible: false, bottomMargin: 2);
    }

    public void ShowLlamaCatalogLoading()
    {
        if (getScreen() is not { IsActive: true } screen) return;
        var theme = screen.CurrentTheme;
        var width = screen.TerminalWidth;
        var lines = new[]
        {
            TerminalDialogPanels.PanelBorder(theme, width),
            TerminalDialogPanels.PadPanelLine(" " + theme.Style("accent", "llama.cpp models", bold: true), width),
            new string(' ', width),
            TerminalDialogPanels.PadPanelLine(" " + theme.Style("muted", "Loading…"), width),
            new string(' ', width),
            TerminalDialogPanels.PanelBorder(theme, width)
        };
        screen.SetEditorPanel(lines, lines.Length - 2, width + 1, cursorVisible: false, bottomMargin: 2,
            flushPendingRenderOnOpen: false);
    }
}
