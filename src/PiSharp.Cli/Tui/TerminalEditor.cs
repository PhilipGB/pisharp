namespace PiSharp.Cli.Tui;

internal sealed record TerminalProgressState(string Message, double? Ratio = null, string? Detail = null);
internal readonly record struct TerminalProgressOutcome<T>(bool Cancelled, T? Value);

/// <summary>Normal-screen terminal editor. Leaves transcript in the terminal scrollback.</summary>
public sealed class TerminalEditor
{
    private static readonly TimeSpan InitialProgressFrameDelay = TimeSpan.FromMilliseconds(16);
    private const string ActiveRunFooter = "Enter steers · follow-up queues · Escape aborts · Alt+Up restores queued input · Ctrl+O tools";
    private readonly EditorBuffer _buffer;
    private TerminalInput? _input;
    private readonly EditorCompletion _completion;
    private readonly EditorKeymap _keymap;
    private TerminalScreen? _screen;
    private bool _searchingTranscript;
    private string _savedDraft = "";
    private int _savedCursor;
    private string _lastSearchQuery = "";
    private bool _toolResultsExpanded;
    public TerminalEditor(Func<IReadOnlyList<string>>? commands = null, string? agentDirectory = null,
        Func<string>? getWorkingDirectory = null)
    {
        _completion = getWorkingDirectory is null
            ? new EditorCompletion(Environment.CurrentDirectory, commands)
            : new EditorCompletion(getWorkingDirectory, commands);
        _keymap = new(agentDirectory);
        _buffer = new(_keymap);
    }
    private const string Prompt = "❯ ";
    private int _renderedRows;
    private int _cursorRow;

    public string Draft => _buffer.Text;
    public string Hotkeys => _keymap.FormatHotkeys();
    internal TerminalTheme CurrentTheme => _screen?.CurrentTheme ?? TerminalTheme.Default;
    internal bool TryReadLoginAbort() => EnsureInput().TryRead(50, out var input) &&
        input.Key is { } key && _keymap.Matches("app.interrupt", key);

    public void ReloadKeybindings() => _keymap.Reload();

    public void AttachScreen(TerminalScreen? screen)
    {
        _screen = screen;
        if (screen is not null)
        {
            screen.SetToolResultsExpanded(_toolResultsExpanded);
            screen.SetEditor(_buffer.Text, _buffer.Cursor, _buffer.SelectionStart, _buffer.SelectionEnd);
        }
    }

    private TerminalInput EnsureInput()
    {
        if (_input is not null)
        {
            _screen?.AttachTerminalInput(_input);
            return _input;
        }
        _input = TerminalInput.OpenConsole();
        _input.TerminalColorReceived += HandleTerminalColorResponse;
        _input.TerminalDeviceAttributesReceived += HandleTerminalDeviceAttributes;
        _input.TerminalColorSchemeReceived += HandleTerminalColorScheme;
        _screen?.AttachTerminalInput(_input);
        return _input;
    }

    private void HandleTerminalColorResponse(TerminalColorResponse response) =>
        _screen?.HandleTerminalColorResponse(response);

    private void HandleTerminalDeviceAttributes() => _screen?.HandleTerminalDeviceAttributes();

    private void HandleTerminalColorScheme(string appearance) => _screen?.HandleTerminalColorScheme(appearance);

    internal void CompleteInitialTerminalColorQuery()
    {
        if (_screen is { IsActive: true } screen)
            screen.CompleteInitialTerminalColorQuery(EnsureInput());
    }

    internal TerminalSelection<T>? ShowSelectionList<T>(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey = null,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items")
    {
        if (_screen is not { IsActive: true } screen) return null;
        var input = EnsureInput();
        using var mode = TerminalMode.Enter(screen);
        return new TerminalOverlayHost(input).Select(screen, title, options, selectedKey, scopedOptions,
            allLabel, scopedLabel, emptyMessage);
    }

    internal TerminalSelection<T>? ShowModelSelectionList<T>(
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey, string hint,
        Func<T, string> modelName, IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        Func<CancellationToken, Task<TerminalSelectionRefresh<T>>>? refreshModels = null,
        CancellationToken cancellationToken = default)
    {
        if (_screen is not { IsActive: true } screen) return null;
        var input = EnsureInput();
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

    internal TerminalSelection<T>? ShowInlineSelectionList<T>(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, IReadOnlyList<string>? header = null,
        string footer = "↑↓ move • enter select • escape/ctrl+c close", string? selectedKey = null,
        bool preservePanelAfterSelection = false, int optionIndent = 0, int bottomMargin = 2,
        int bottomSpacerLines = 0)
    {
        if (_screen is not { IsActive: true } screen || options.Count == 0) return null;
        var input = EnsureInput();
        var selectedIndex = selectedKey is null ? 0 : Math.Max(0,
            Array.FindIndex(options.ToArray(), option => option.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase)));
        using var mode = TerminalMode.Enter(screen);
        var preservePanel = false;
        try
        {
            while (screen.IsActive)
            {
                screen.RefreshIfResized();
                var panel = RenderInlinePanel(screen, title, options, selectedIndex, header, footer,
                    optionIndent, bottomSpacerLines);
                screen.SetEditorPanel(panel, panel.Count - 2, screen.TerminalWidth + 1, cursorVisible: false,
                    bottomMargin);
                var next = input.Read();
                if (next.IsEndOfStream) return null;
                if (next.Key is not { } key) continue;
                if (key.Key == ConsoleKey.Escape || _keymap.Matches("app.interrupt", key) ||
                    _keymap.Matches("app.clear", key)) return null;
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
            }
            return null;
        }
        finally { if (!preservePanel) screen.SetEditorPanel(null); }
    }

    internal void ShowInlineSelectionPanel<T>(string title, IReadOnlyList<TerminalSelectionOption<T>> options,
        IReadOnlyList<string>? header = null, string footer = "↑↓ move • enter select • escape/ctrl+c close",
        int selectedIndex = 0)
    {
        if (_screen is not { IsActive: true } screen || options.Count == 0) return;
        var panel = RenderInlinePanel(screen, title, options, Math.Clamp(selectedIndex, 0, options.Count - 1), header, footer);
        screen.SetEditorPanel(panel, panel.Count - 2, screen.TerminalWidth + 1, cursorVisible: false, bottomMargin: 2);
    }

    internal async Task<IReadOnlyList<string>?> PromptSequenceAsync(string title,
        IReadOnlyList<(string Message, string? Placeholder)> prompts, bool preservePanelAfterSubmit = false)
    {
        if (prompts.Count == 0) return [];
        if (_screen is not { IsActive: true } screen)
        {
            var redirectedValues = new List<string>(prompts.Count);
            foreach (var prompt in prompts)
            {
                Console.Error.WriteLine(prompt.Message);
                var value = await ReadLineAsync(_ => Task.CompletedTask, enableApplicationActions: false,
                    allowEmptySubmit: true).ConfigureAwait(false);
                if (value is null) return null;
                redirectedValues.Add(value);
            }
            return redirectedValues;
        }

        var values = new List<string>(prompts.Count);
        var input = EnsureInput();
        var preservePanel = false;
        using var mode = TerminalMode.Enter(screen);
        try
        {
            var titleTheme = screen.CurrentTheme;
            var titleBorder = titleTheme.Fg("border") + new string('─', Math.Max(1, screen.TerminalWidth)) + "\u001b[0m";
            var titlePanel = new[]
            {
                titleBorder,
                PadPanelLine(" " + titleTheme.Style("accent", TerminalSafeText.Normalize(title), bold: true),
                    screen.TerminalWidth),
                titleBorder
            };
            screen.SetEditorPanel(titlePanel, cursorVisible: false, bottomMargin: 2);
            for (var promptIndex = 0; promptIndex < prompts.Count; promptIndex++)
            {
                var promptBuffer = new EditorBuffer(_keymap);
                var needsRender = true;
                var renderedWidth = -1;
                var renderedHeight = -1;
                while (screen.IsActive)
                {
                    screen.RefreshIfResized();
                    if (renderedWidth != screen.TerminalWidth || renderedHeight != screen.TerminalHeight)
                        needsRender = true;
                    if (needsRender)
                    {
                        var panel = RenderPromptPanel(screen, title, prompts, values, promptIndex, promptBuffer.Text,
                            promptBuffer.Cursor, out var cursorRow, out var cursorColumn);
                        screen.SetEditorPanel(panel, cursorRow, cursorColumn, cursorVisible: false, bottomMargin: 2);
                        renderedWidth = screen.TerminalWidth;
                        renderedHeight = screen.TerminalHeight;
                        needsRender = false;
                    }

                    if (!input.TryRead(40, out var next)) continue;
                    if (next.IsEndOfStream) return null;
                    if (next.Key is { } key && (key.Key == ConsoleKey.Escape ||
                        _keymap.Matches("app.interrupt", key) || _keymap.Matches("app.clear", key))) return null;
                    if (next.Key is { } submit && submit.Key == ConsoleKey.Enter)
                    {
                        values.Add(promptBuffer.Text);
                        var submittedPanel = RenderPromptPanel(screen, title, prompts, values, promptIndex,
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
                if (!screen.IsActive) return null;
            }
            preservePanel = preservePanelAfterSubmit;
            return values;
        }
        finally
        {
            if (!preservePanel) screen.SetEditorPanel(null);
        }
    }

    internal void DismissEditorPanel() => _screen?.SetEditorPanel(null);

    internal void DismissEditorPanelAndAppendStatus(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info)
    {
        if (_screen is { IsActive: true } screen) screen.DismissEditorPanelAndAppendStatus(message, kind);
        else Console.WriteLine(message);
    }

    internal string? ShowHuggingFaceSearch(HuggingFaceClient client,
        IDictionary<string, IReadOnlyList<HuggingFaceModel>> cache)
    {
        if (_screen is not { IsActive: true } screen) return null;
        return new TerminalHuggingFaceSearch(screen, EnsureInput(), _keymap, client, cache).Show();
    }

    internal void ShowInlineStatus(string title, string message)
    {
        if (_screen is not { IsActive: true } screen) return;
        var theme = screen.CurrentTheme;
        var width = screen.TerminalWidth;
        var lines = new[]
        {
            PanelBorder(theme, width),
            PadPanelLine(" " + theme.Style("accent", TerminalSafeText.Normalize(title), bold: true), width),
            "",
            PadPanelLine(" " + theme.Style("muted", TerminalSafeText.Normalize(message)), width),
            PanelBorder(theme, width)
        };
        screen.SetEditorPanel(lines, lines.Length - 2, width + 1, cursorVisible: false, bottomMargin: 2);
    }

    internal void ShowLlamaCatalogLoading()
    {
        if (_screen is not { IsActive: true } screen) return;
        var theme = screen.CurrentTheme;
        var width = screen.TerminalWidth;
        var lines = new[]
        {
            PanelBorder(theme, width),
            PadPanelLine(" " + theme.Style("accent", "llama.cpp models", bold: true), width),
            new string(' ', width),
            PadPanelLine(" " + theme.Style("muted", "Loading…"), width),
            new string(' ', width),
            PanelBorder(theme, width)
        };
        screen.SetEditorPanel(lines, lines.Length - 2, width + 1, cursorVisible: false, bottomMargin: 2,
            flushPendingRenderOnOpen: false);
    }

    internal void SetStatusNotification(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info) => _screen?.SetStatusNotification(message, kind);

    internal void AppendStatusMessage(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info)
    {
        if (_screen is { IsActive: true } screen) screen.AppendStatusMessage(message, kind);
        else Console.WriteLine(message);
    }

    private static IReadOnlyList<string> RenderInlinePanel<T>(TerminalScreen screen, string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, int selectedIndex, IReadOnlyList<string>? header,
        string footer, int optionIndent = 0, int bottomSpacerLines = 0)
    {
        var width = screen.TerminalWidth;
        var theme = screen.CurrentTheme;
        var lines = new List<string> { PanelBorder(theme, width) };
        var titleRows = TerminalSafeText.Normalize(title).Split('\n');
        for (var index = 0; index < titleRows.Length; index++)
        {
            var row = " " + theme.Style("accent", titleRows[index], bold: true);
            if (titleRows.Length > 1 && index < titleRows.Length - 1) row += theme.Fg("accent");
            lines.Add(PadPanelLine(row, width) +
                (titleRows.Length > 1 && index < titleRows.Length - 1 ? "\u001b[39m" : ""));
        }
        if (header is not null)
        {
            foreach (var row in header)
                lines.Add(row.Length == 0 ? "" : PadPanelLine(" " + theme.Style("dim", TerminalSafeText.Normalize(row)), width));
        }
        if (options.Count == 0)
            lines.Add(PadPanelLine(" " + theme.Style("warning", "No router models are available"), width));
        else
        {
            var labelWidth = Math.Clamp(Math.Max(34, options.Max(option => TerminalTextLayout.Width(option.Label))), 34, 54);
            foreach (var (option, index) in options.Select((option, index) => (option, index)))
            {
                var label = TerminalSafeText.Normalize(option.Label);
                var marker = index == selectedIndex ? "→ " : "  ";
                var selected = index == selectedIndex;
                var current = option.IsCurrent ? theme.Style("accent", "✓ ") : "  ";
                var description = string.IsNullOrWhiteSpace(option.Description)
                    ? "" : TerminalSafeText.Normalize(option.Description);
                var primary = new string(' ', Math.Max(0, optionIndent)) + marker + current + label;
                if (description.Length == 0)
                    lines.Add(selected ? theme.Style("accent", primary) : primary);
                else if (selected)
                    lines.Add(theme.Style("accent", marker + label.PadRight(labelWidth) + "  " + description));
                else
                    lines.Add(primary + theme.Style("muted",
                        new string(' ', Math.Max(2, labelWidth - TerminalTextLayout.Width(label) + 2)) + description));
            }
        }
        lines.Add("");
        lines.Add(PadPanelLine(" " + RenderKeyHint(theme, TerminalSafeText.Normalize(footer)), width));
        lines.AddRange(Enumerable.Repeat("", Math.Max(0, bottomSpacerLines)));
        lines.Add(PanelBorder(theme, width));
        return lines;
    }

    private static string RenderKeyHint(TerminalTheme theme, string value)
    {
        var groups = value.Split(" • ", StringSplitOptions.None);
        for (var index = 0; index < groups.Length; index++)
        {
            var separator = groups[index].IndexOf(' ');
            groups[index] = separator <= 0
                ? theme.Style("dim", groups[index])
                : theme.Style("dim", groups[index][..separator]) +
                  theme.Style("muted", groups[index][separator..]);
        }
        return string.Join(" • ", groups);
    }

    private static IReadOnlyList<string> RenderPromptPanel(TerminalScreen screen, string title,
        IReadOnlyList<(string Message, string? Placeholder)> prompts, IReadOnlyList<string> completed,
        int promptIndex, string value, int cursor, out int cursorRow, out int cursorColumn,
        bool submittedPrompt = false)
    {
        var width = screen.TerminalWidth;
        var theme = screen.CurrentTheme;
        var border = theme.Fg("border") + new string('─', Math.Max(1, width)) + "\u001b[0m";
        var lines = new List<string> { border,
            PadPanelLine(" " + theme.Style("accent", TerminalSafeText.Normalize(title), bold: true), width) };
        var currentInputRow = 0;
        for (var index = 0; index <= promptIndex; index++)
        {
            lines.Add("");
            lines.Add(PadPanelLine(" " + theme.Style("text", TerminalSafeText.Normalize(prompts[index].Message)), width));
            if (!string.IsNullOrWhiteSpace(prompts[index].Placeholder))
                lines.Add(PadPanelLine(" " + theme.Style("dim", "e.g., " + TerminalSafeText.Normalize(prompts[index].Placeholder!)), width));
            currentInputRow = lines.Count;
            var inputValue = TerminalSafeText.Normalize(index == promptIndex ? value : completed[index]);
            var inputLine = "> " + inputValue;
            if (index == promptIndex && !submittedPrompt)
            {
                var inputCursor = Math.Clamp(cursor, 0, inputValue.Length);
                var cursorCharacter = inputCursor < inputValue.Length ? inputValue[inputCursor].ToString() : " ";
                inputLine = "> " + inputValue[..inputCursor] + "\u001b[7m" + cursorCharacter + "\u001b[27m" +
                    inputValue[(inputCursor + (inputCursor < inputValue.Length ? 1 : 0))..];
            }
            lines.Add(PadPanelLine(inputLine, width));
            lines.Add(PadPanelLine(" (" + theme.Style("dim", "escape/ctrl+c") +
                theme.Style("muted", " to cancel,") + " " + theme.Style("dim", "enter") +
                theme.Style("muted", " to submit") + ")", width));
        }
        lines.Add(border);
        cursorRow = currentInputRow;
        var normalizedValue = TerminalSafeText.Normalize(value);
        cursorColumn = Math.Min(width, 3 + TerminalTextLayout.Width(normalizedValue[..Math.Clamp(cursor, 0, normalizedValue.Length)]));
        return lines;
    }

    private static string PanelBorder(TerminalTheme theme, int width) =>
        theme.Fg("accent") + new string('─', Math.Max(1, width)) + "\u001b[0m";

    private static string PadPanelLine(string line, int width) =>
        line + new string(' ', Math.Max(0, width - TerminalTextLayout.Width(line)));

    internal async Task<TerminalProgressOutcome<T>> RunProgressAsync<T>(string title, string subject,
        string cancelTitle, string cancelMessage,
        Func<CancellationToken, Action<TerminalProgressState>, Task<T>> run, Func<Task> cancelOperation,
        Func<Task>? afterCancelled = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(cancelOperation);
        if (_screen is not { IsActive: true } screen)
            throw new InvalidOperationException("Progress requires an active terminal screen.");

        using var mode = TerminalMode.Enter(screen);
        using var cancellation = new CancellationTokenSource();
        var input = EnsureInput();
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
                PanelBorder(theme, width),
                PadPanelLine(" " + theme.Style("accent", TerminalSafeText.Normalize(title), bold: true), width),
                PadPanelLine(" " + theme.Style("text", TerminalSafeText.Normalize(subject)), width),
                "",
                PadPanelLine(" " + theme.Style("muted", TerminalSafeText.Normalize(state.Message)), width)
            };
            if (state.Ratio is { } ratio)
            {
                var bounded = Math.Clamp(ratio, 0, 1);
                var filled = (int)Math.Round(bounded * 40, MidpointRounding.AwayFromZero);
                lines.Add(PadPanelLine(" " + theme.Style("accent", new string('█', filled) + new string('─', 40 - filled) +
                    $" {Math.Round(bounded * 100):0}%"), width));
            }
            if (!string.IsNullOrEmpty(state.Detail))
                lines.Add(PadPanelLine(" " + theme.Style("dim", TerminalSafeText.Normalize(state.Detail)), width));
            lines.Add("");
            lines.Add(PadPanelLine(" " + RenderKeyHint(theme, "escape/ctrl+c stop"), width));
            lines.Add(PanelBorder(theme, width));
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
                if (next.Key is not { } key ||
                    !_keymap.Matches("app.interrupt", key) && !_keymap.Matches("app.clear", key)) continue;
                var confirmation = ShowInlineSelectionList(confirmationTitle, cancelOptions, [""],
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
                    ShowInlineSelectionPanel(confirmationTitle, cancelOptions, [""], confirmationFooter);
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

    /// <summary>Seed the next editable prompt after a session fork; never submits it automatically.</summary>
    public void Prefill(string text) => _buffer.SetText(text);

    public void InsertTextAtCursor(string text)
    {
        _buffer.InsertText(text);
        Render();
    }

    /// <summary>Return queued messages to the editor without discarding a draft typed during the run.</summary>
    public void RestorePending(IEnumerable<string> messages)
    {
        var restored = messages.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (!string.IsNullOrWhiteSpace(_buffer.Text)) restored.Add(_buffer.Text);
        _buffer.SetText(string.Join("\n\n", restored));
    }

    /// <summary>Read active-run controls while provider output streams. Enter steers, Alt+Enter
    /// follows up, Alt+Up restores queued input, and Escape aborts after restoring queued input.</summary>
    public async Task MonitorRunAsync(Func<string, bool, CancellationToken, Task<bool>> queue,
        Func<IReadOnlyList<string>> clearQueue, Action abort, CancellationToken cancellationToken,
        Func<string, Task>? dispatchApplicationAction = null)
    {
        try
        {
            using var mode = TerminalMode.Enter(_screen);
            var input = EnsureInput();
            _screen?.SetFooter(ActiveRunFooter);
            Render();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!input.TryRead(50, out var next))
                {
                    _screen?.RefreshIfResized();
                    await Task.Delay(10, CancellationToken.None);
                    continue;
                }
                if (!await HandleActiveInputAsync(next, queue, clearQueue, abort, cancellationToken, dispatchApplicationAction)) break;
                Render();
            }
        }
        finally
        {
            if (_searchingTranscript) CloseTranscriptSearch();
            ClearLine();
        }
    }

    /// <summary>Apply one active-run key event. Exposed separately for deterministic input tests.</summary>
    public async Task<bool> HandleActiveInputAsync(TerminalInputEvent next,
        Func<string, bool, CancellationToken, Task<bool>> queue, Func<IReadOnlyList<string>> clearQueue,
        Action abort, CancellationToken cancellationToken = default,
        Func<string, Task>? dispatchApplicationAction = null)
    {
        if (next.IsEndOfStream) return false;
        if (next.IsControl) return true;
        if (next.Mouse is { } mouse)
        {
            var result = _screen?.HandleMouse(mouse) ?? default;
            if (result.ClearEditorSelection)
            {
                _buffer.ClearSelection();
                Render();
            }
            if (result.EditorCursorOffset is { } cursor)
            {
                _buffer.SetCursor(cursor);
                Render();
            }
            if (result.Copy && dispatchApplicationAction is not null)
                await dispatchApplicationAction("app.message.copy");
            return true;
        }
        if (_searchingTranscript)
        {
            if (next.Key is { } searchKey && _keymap.Matches("app.tools.expand", searchKey))
            {
                ToggleToolResults();
                return true;
            }
            return HandleTranscriptSearchInput(next);
        }
        if (next.Key is { } completionKey && _keymap.Matches("tui.input.tab", completionKey))
        {
            try
            {
                CompleteInput(completionKey);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"Completion unavailable: {error.Message}");
            }
            return true;
        }
        if (next.Key is { } key && (_keymap.Matches("app.interrupt", key) || _keymap.Matches("app.clear", key)))
        {
            RestorePending(clearQueue());
            abort();
            return false;
        }
        if (next.Key is { } dequeueKey && _keymap.Matches("app.message.dequeue", dequeueKey))
        {
            RestorePending(clearQueue());
            return true;
        }
        if (next.Key is { } screenKey && _screen is { IsActive: true } screen)
        {
            if (_keymap.Matches("tui.altScreen.search", screenKey))
            {
                StartTranscriptSearch();
                return true;
            }
            if (_keymap.Matches("app.tools.expand", screenKey))
            {
                ToggleToolResults();
                return true;
            }
            if (_keymap.Matches("tui.altScreen.pageUp", screenKey))
            {
                screen.ScrollPage(up: true);
                return true;
            }
            if (_keymap.Matches("tui.altScreen.pageDown", screenKey))
            {
                screen.ScrollPage(up: false);
                return true;
            }
            if (_keymap.Matches("tui.altScreen.top", screenKey))
            {
                screen.ScrollToTop();
                return true;
            }
            if (_keymap.Matches("tui.altScreen.bottom", screenKey))
            {
                screen.ScrollToBottom();
                return true;
            }
        }
        if (next.Key is { } clipboardKey && dispatchApplicationAction is not null && !_keymap.MatchesEditorAction(clipboardKey))
        {
            if (_keymap.Matches("app.message.copy", clipboardKey))
            {
                await dispatchApplicationAction("app.message.copy");
                return true;
            }
            if (_keymap.Matches("app.clipboard.pasteImage", clipboardKey))
            {
                await dispatchApplicationAction("app.clipboard.pasteImage");
                return true;
            }
        }
        if (next.Key is { } inputKey)
        {
            if (_keymap.Matches("tui.input.newLine", inputKey) ||
                inputKey.Key == ConsoleKey.Enter && inputKey.Modifiers.HasFlag(ConsoleModifiers.Alt) &&
                !_keymap.Matches("app.message.followUp", inputKey))
            {
                _ = _buffer.Handle(inputKey);
                return true;
            }
            var followUp = _keymap.Matches("app.message.followUp", inputKey);
            if (followUp || _keymap.Matches("tui.input.submit", inputKey))
            {
                if (!_buffer.TrySubmit(out var text)) return true;
                _buffer.Clear();
                if (!await queue(text, followUp, cancellationToken)) RestorePending([text]);
                return true;
            }
        }
        _ = next.Text is not null ? _buffer.InsertText(next.Text) : _buffer.Handle(next.Key!.Value);
        return true;
    }

    private void ToggleToolResults()
    {
        if (_screen is not { IsActive: true } screen) return;
        _toolResultsExpanded = screen.ToggleToolResultsExpanded();
        if (_searchingTranscript) UpdateTranscriptSearch();
        else screen.SetFooter($"{ActiveRunFooter} · Tool output {(_toolResultsExpanded ? "expanded" : "collapsed")}");
    }

    private void StartTranscriptSearch()
    {
        if (_screen is not { IsActive: true }) return;
        _savedDraft = _buffer.Text;
        _savedCursor = _buffer.Cursor;
        _buffer.SetText(_lastSearchQuery);
        _searchingTranscript = true;
        UpdateTranscriptSearch();
    }

    private bool HandleTranscriptSearchInput(TerminalInputEvent next)
    {
        if (_screen is not { IsActive: true })
        {
            CloseTranscriptSearch();
            return true;
        }
        if (next.Key is { } key)
        {
            if (_keymap.Matches("tui.altScreen.searchClose", key) || _keymap.Matches("app.interrupt", key))
            {
                CloseTranscriptSearch();
                return true;
            }
            if (_keymap.Matches("tui.altScreen.searchNext", key))
            {
                UpdateTranscriptSearch(direction: 1);
                return true;
            }
            if (_keymap.Matches("tui.altScreen.searchPrevious", key))
            {
                UpdateTranscriptSearch(direction: -1);
                return true;
            }
        }

        if (next.Text is not null) _buffer.InsertText(next.Text);
        else if (next.Key is { } editKey) _ = _buffer.Handle(editKey);
        UpdateTranscriptSearch();
        return true;
    }

    private void UpdateTranscriptSearch(int direction = 0)
    {
        if (_screen is not { IsActive: true } screen) return;
        var query = _buffer.Text;
        var state = screen.SearchTranscript(query, direction);
        var count = state.HasMoreMatches ? $"{state.MatchCount}+" : state.MatchCount.ToString();
        var status = query.Length == 0 ? "type to search" : state.MatchCount == 0 ? "no matches" : $"{state.SelectedMatch}/{count}";
        screen.SetFooter($"Search: {query} · {status} · Enter next · Shift+Enter previous · Escape close");
    }

    private void CloseTranscriptSearch()
    {
        _lastSearchQuery = _buffer.Text;
        _searchingTranscript = false;
        _buffer.SetText(_savedDraft, _savedCursor);
        _screen?.ClearTranscriptSearch();
        _screen?.SetFooter(ActiveRunFooter);
    }

    public async Task<string?> ReadLineAsync(Func<string, Task> dispatchApplicationAction,
        bool enableApplicationActions = true, bool allowEmptySubmit = false)
    {
        ArgumentNullException.ThrowIfNull(dispatchApplicationAction);
        using var mode = TerminalMode.Enter(_screen);
        var input = EnsureInput();
        Render();
        while (true)
        {
            var next = input.Read();
            if (next.Mouse is { } mouse)
            {
                var result = _screen?.HandleMouse(mouse) ?? default;
                if (result.EditorCursorOffset is { } cursor)
                {
                    _buffer.SetCursor(cursor);
                    Render();
                }
                if (result.Copy)
                    await dispatchApplicationAction("app.message.copy");
                continue;
            }
            if (next.IsEndOfStream)
            {
                ClearLine();
                Console.WriteLine();
                return null;
            }
            if (enableApplicationActions && next.Key is { } externalEditorKey &&
                !_keymap.MatchesEditorAction(externalEditorKey) &&
                _keymap.MatchIdleApplicationAction(externalEditorKey) is { } externalAction && externalAction == "app.editor.external")
            {
                ClearLine();
                mode.Suspend();
                try
                {
                    _screen?.Suspend();
                    await dispatchApplicationAction(externalAction);
                }
                finally
                {
                    try { _screen?.Resume(); }
                    finally { mode.Resume(); }
                }
                Render();
                continue;
            }
            if (enableApplicationActions && next.Key is { } applicationKey &&
                await HandleApplicationShortcutAsync(applicationKey, dispatchApplicationAction, ClearLine))
            {
                Render();
                continue;
            }
            if (allowEmptySubmit && next.Key is { } submitKey &&
                _keymap.Matches("tui.input.submit", submitKey) && string.IsNullOrWhiteSpace(_buffer.Text))
            {
                var submitted = _buffer.Text;
                ClearLine();
                Console.WriteLine($"{Prompt}{submitted}");
                _buffer.Clear();
                return submitted;
            }
            if (next.Key is { } tabKey && _keymap.Matches("tui.input.tab", tabKey))
            {
                try
                {
                    CompleteInput(tabKey);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    ClearLine();
                    Console.WriteLine($"\nCompletion unavailable: {error.Message}");
                }
                Render();
                continue;
            }
            EditorAction action;
            if (TerminalInput.TryGetText(next, out _))
            {
                var text = input.ReadAvailableText(next, out var following);
                action = _buffer.InsertText(text);
                if (following?.Key is { } bufferedSubmitKey && _keymap.Matches("tui.input.submit", bufferedSubmitKey))
                {
                    next = following;
                    action = EditorAction.Submit;
                }
                else
                {
                    if (following is not null) input.PushBack(following);
                    if (action == EditorAction.Render) Render();
                    continue;
                }
            }
            else action = _buffer.Handle(next.Key!.Value);
            switch (action)
            {
                case EditorAction.Exit:
                    ClearLine();
                    Console.WriteLine();
                    return null;
                case EditorAction.Cancel:
                    Render();
                    break;
                case EditorAction.Submit:
                    var submitted = _buffer.Text;
                    ClearLine();
                    if (!submitted.StartsWith("/", StringComparison.Ordinal))
                        Console.WriteLine($"{Prompt}{new string(submitted.Replace('\n', '↵').Select(c => char.IsControl(c) ? ' ' : c).ToArray())}");
                    _buffer.Clear();
                    return submitted;
                case EditorAction.Render: Render(); break;
            }
        }
    }

    public async Task<bool> HandleApplicationShortcutAsync(ConsoleKeyInfo key, Func<string, Task> dispatch,
        Action? beforeDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (_keymap.MatchesEditorAction(key)) return false;
        var action = _keymap.MatchIdleApplicationAction(key);
        if (action is null) return false;
        beforeDispatch?.Invoke();
        await dispatch(action);
        return true;
    }

    private void CompleteInput(ConsoleKeyInfo key)
    {
        var matches = _completion.Complete(_buffer);
        if (matches.Count == 0)
        {
            _buffer.Handle(key);
            return;
        }
        if (matches.Count == 1) return;

        if (_screen is { IsActive: true })
        {
            var options = matches.Select((match, index) => new TerminalSelectionOption<string>(
                index.ToString(), match, match)).ToArray();
            var selected = ShowSelectionList("Complete input", options, emptyMessage: "No matching completions");
            if (selected is not null) _completion.ApplySelected(_buffer, selected.Option.Value);
            return;
        }

        ClearLine();
        Console.WriteLine();
        Console.WriteLine(string.Join("  ", matches.Take(10).Select(match =>
            new string(match.Select(c => char.IsControl(c) ? ' ' : c).ToArray()))) +
            (matches.Count > 10 ? "  …" : ""));
    }

    private void ClearLine()
    {
        if (_screen is { IsActive: true })
        {
            _screen.SetEditor("", 0);
            return;
        }
        if (_renderedRows == 0) { Console.Write("\r\u001b[2K"); return; }
        if (_cursorRow > 0) Console.Write($"\u001b[{_cursorRow}A");
        for (var row = 0; row < _renderedRows; row++)
        {
            Console.Write("\r\u001b[2K");
            if (row < _renderedRows - 1) Console.Write("\u001b[1B");
        }
        if (_renderedRows > 1) Console.Write($"\u001b[{_renderedRows - 1}A");
        Console.Write("\r");
        _renderedRows = 0;
        _cursorRow = 0;
    }

    private void Render()
    {
        if (_screen is { IsActive: true })
        {
            _screen.SetEditor(_buffer.Text, _buffer.Cursor, _buffer.SelectionStart, _buffer.SelectionEnd);
            return;
        }
        var width = TerminalScreen.ReadColumns();
        var height = TerminalScreen.ReadRows();
        var frame = EditorViewport.Layout(_buffer.Text, _buffer.Cursor, width, Math.Max(1, height / 3));
        ClearLine();
        for (var row = 0; row < frame.Rows.Count; row++)
        {
            if (row > 0) Console.Write("\n");
            Console.Write(frame.Rows[row]);
        }
        _renderedRows = frame.Rows.Count;
        _cursorRow = frame.CursorRow;
        var up = frame.Rows.Count - 1 - frame.CursorRow;
        if (up > 0) Console.Write($"\u001b[{up}A");
        Console.Write($"\r\u001b[{frame.CursorColumn}G");
    }
}
