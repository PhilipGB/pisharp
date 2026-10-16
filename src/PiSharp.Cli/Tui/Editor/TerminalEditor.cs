namespace PiSharp.Cli.Tui;

/// <summary>Edits drafts and routes idle and active-run input to application actions and terminal dialogs.</summary>
public sealed class TerminalEditor
{
    private sealed record CompletionCycle(IReadOnlyList<string> Candidates, int SelectedIndex,
        string BufferText, int Cursor)
    {
        public bool Matches(EditorBuffer buffer) => BufferText == buffer.Text && Cursor == buffer.Cursor;
    }

    private readonly EditorBuffer _buffer;
    private TerminalInput? _input;
    private readonly EditorCompletion _completion;
    private readonly EditorKeymap _keymap;
    private readonly TerminalDialogs _dialogs;
    private readonly TerminalProgressDisplay _progress;
    private readonly TerminalTranscriptSearchInput _transcriptSearch;
    private readonly TerminalAuthorizationInput _authorizationInput;
    private TerminalScreen? _screen;
    private string? _lastRenderedEditorText;
    private bool _toolResultsExpanded;
    private long? _lastClearAtMilliseconds;
    private CompletionCycle? _completionCycle;
    public TerminalEditor(Func<IReadOnlyList<string>>? commands = null, string? agentDirectory = null,
        Func<string>? getWorkingDirectory = null, bool initiallyExpanded = false,
        Func<string, string?>? describeCommand = null)
    {
        _completion = getWorkingDirectory is null
            ? new EditorCompletion(Environment.CurrentDirectory, commands, describeCommand)
            : new EditorCompletion(getWorkingDirectory, commands, describeCommand);
        _keymap = new(agentDirectory);
        _buffer = new(_keymap);
        _toolResultsExpanded = initiallyExpanded;
        _dialogs = new(_keymap, EnsureInput, () => _screen,
            token => ReadLineAsync(_ => Task.CompletedTask, enableApplicationActions: false,
                allowEmptySubmit: true, cancellationToken: token));
        _progress = new(_keymap, EnsureInput, () => _screen, _dialogs);
        _transcriptSearch = new(_buffer, _keymap, () => _screen);
        _authorizationInput = new(_keymap, EnsureInput, () => _screen);
    }
    private const string Prompt = "❯ ";
    private int _renderedRows;
    private int _cursorRow;

    public string Draft => _buffer.Text;
    public string Hotkeys => _keymap.FormatHotkeys();
    internal string? GetDisplayKeys(string action) => _keymap.GetDisplayKeys(action);
    internal bool MatchesKeybinding(string action, ConsoleKeyInfo key) => _keymap.Matches(action, key);
    internal TerminalTheme CurrentTheme => _screen?.CurrentTheme ?? TerminalTheme.Default;
    internal string? ActiveAuthorizationUrl => _screen?.ActiveAuthorizationUrl;
    internal bool IsScreenActive => _screen is { IsActive: true };
    internal TextReader McpAuthorizationCallbackInput => _authorizationInput;
    internal bool PresentProviderAuthorizationUrl(string providerId, string providerName,
        string message, Uri authorizationUrl) => _screen?.AppendProviderAuthorizationNotification(
            providerId, providerName, message, authorizationUrl,
            _keymap.GetDisplayKeys("app.message.copy"), _keymap.GetDisplayKeys("app.interrupt")) == true;
    internal void ClearAuthorizationUrl(string owner) => _screen?.ClearAuthorizationUrl(owner);

    internal TerminalLoginInputAction? TryReadLoginAction() =>
        _authorizationInput.TryReadLoginAction();

    public void ReloadKeybindings()
    {
        _keymap.Reload();
        _screen?.SetToolExpandKeyLabel(_keymap.GetDisplayKeys("app.tools.expand"));
    }

    public void AttachScreen(TerminalScreen? screen)
    {
        _screen = screen;
        if (screen is not null)
        {
            screen.SetToolResultsExpanded(_toolResultsExpanded);
            screen.SetToolExpandKeyLabel(_keymap.GetDisplayKeys("app.tools.expand"));
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
        _screen?.AttachTerminalInput(_input);
        return _input;
    }

    internal void CompleteInitialTerminalColorQuery()
    {
        if (_screen is { IsActive: true } screen)
            screen.CompleteInitialTerminalColorQuery(EnsureInput());
    }

    internal TerminalSelection<T>? ShowSelectionList<T>(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey = null,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items",
        string? actionHint = null,
        Func<TerminalSelectionList<T>, TerminalInputEvent, bool>? handleInput = null) =>
        _dialogs.ShowSelectionList(title, options, selectedKey, scopedOptions, allLabel, scopedLabel, emptyMessage,
            actionHint, handleInput);

    internal Task<TerminalSelection<T>?> ShowSelectionListAsync<T>(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey = null,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items",
        string? actionHint = null,
        Func<TerminalSelectionList<T>, TerminalInputEvent, Task<bool>>? handleInput = null,
        CancellationToken cancellationToken = default) =>
        _dialogs.ShowSelectionListAsync(title, options, selectedKey, scopedOptions, allLabel, scopedLabel,
            emptyMessage, actionHint, handleInput, cancellationToken);

    internal TerminalSelection<T>? ShowModelSelectionList<T>(
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey, string hint,
        Func<T, string> modelName, IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        Func<CancellationToken, Task<TerminalSelectionRefresh<T>>>? refreshModels = null,
        CancellationToken cancellationToken = default) =>
        _dialogs.ShowModelSelectionList(options, selectedKey, hint, modelName, scopedOptions, refreshModels, cancellationToken);

    internal TerminalSelection<T>? ShowInlineSelectionList<T>(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, IReadOnlyList<string>? header = null,
        string footer = "↑↓ move • enter select • escape/ctrl+c close", string? selectedKey = null,
        bool preservePanelAfterSelection = false, int optionIndent = 0, int bottomMargin = 2,
        int bottomSpacerLines = 0, bool showCurrentMarker = false,
        CancellationToken cancellationToken = default, bool extensionDialog = false, string headerStyle = "dim") =>
        _dialogs.ShowInlineSelectionList(title, options, header, footer, selectedKey, preservePanelAfterSelection, optionIndent, bottomMargin, bottomSpacerLines, showCurrentMarker, cancellationToken, extensionDialog, headerStyle);

    internal void ShowInlineSelectionPanel<T>(string title, IReadOnlyList<TerminalSelectionOption<T>> options,
        IReadOnlyList<string>? header = null, string footer = "↑↓ move • enter select • escape/ctrl+c close",
        int selectedIndex = 0) =>
        _dialogs.ShowInlineSelectionPanel(title, options, header, footer, selectedIndex);

    internal Task<IReadOnlyList<string>?> PromptSequenceAsync(string title,
        IReadOnlyList<(string Message, string? Placeholder)> prompts, bool preservePanelAfterSubmit = false,
        bool preservePanelOnCancel = false, CancellationToken cancellationToken = default,
        bool extensionDialog = false, Func<string, Task>? dispatchApplicationAction = null,
        IReadOnlyList<string>? initialValues = null) =>
        _dialogs.PromptSequenceAsync(title, prompts, preservePanelAfterSubmit, preservePanelOnCancel, cancellationToken,
            extensionDialog, dispatchApplicationAction, initialValues);

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

    internal void ShowInlineStatus(string title, string message) =>
        _dialogs.ShowInlineStatus(title, message);

    internal void ShowLlamaCatalogLoading() =>
        _dialogs.ShowLlamaCatalogLoading();

    internal void SetStatusNotification(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info) => _screen?.SetStatusNotification(message, kind);

    internal void AppendStatusMessage(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info)
    {
        if (_screen is { IsActive: true } screen) screen.AppendStatusMessage(message, kind);
        else Console.WriteLine(message);
    }

    internal Task<TerminalProgressOutcome<T>> RunProgressAsync<T>(string title, string subject,
        string cancelTitle, string cancelMessage,
        Func<CancellationToken, Action<TerminalProgressState>, Task<T>> run, Func<Task> cancelOperation,
        Func<Task>? afterCancelled = null, bool confirmCancellation = true) =>
        _progress.RunProgressAsync(title, subject, cancelTitle, cancelMessage, run, cancelOperation, afterCancelled, confirmCancellation);

    /// <summary>Seed the next editable prompt after a session fork; never submits it automatically.</summary>
    public void Prefill(string text)
    {
        _completionCycle = null;
        _buffer.SetText(text);
    }

    public void InsertTextAtCursor(string text)
    {
        _completionCycle = null;
        _buffer.InsertText(text);
        Render();
    }

    /// <summary>Return queued messages to the editor without discarding a draft typed during the run.</summary>
    public void RestorePending(IEnumerable<string> messages)
    {
        _completionCycle = null;
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
            Render();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!input.TryReadActive(50, cancellationToken, out var next))
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
            if (_transcriptSearch.IsActive) _transcriptSearch.Close();
            ClearLine();
        }
    }

    /// <summary>Apply one active-run key event. Exposed separately for deterministic input tests.</summary>
    public async Task<bool> HandleActiveInputAsync(TerminalInputEvent next,
        Func<string, bool, CancellationToken, Task<bool>> queue, Func<IReadOnlyList<string>> clearQueue,
        Action abort, CancellationToken cancellationToken = default,
        Func<string, Task>? dispatchApplicationAction = null)
    {
        var isCompletionKey = next.Key is { } possibleCompletionKey &&
            _keymap.Matches("tui.input.tab", possibleCompletionKey);
        if (!isCompletionKey) _completionCycle = null;
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
        if (_transcriptSearch.IsActive)
        {
            if (next.Key is { } searchKey && _keymap.Matches("app.tools.expand", searchKey))
            {
                ToggleToolResults();
                return true;
            }
            return _transcriptSearch.HandleInput(next);
        }
        if (next.Key is { } completionKey && _keymap.Matches("tui.input.tab", completionKey))
        {
            try
            {
                CompleteInput(completionKey);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _completionCycle = null;
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
                _transcriptSearch.Start();
                return true;
            }
            if (dispatchApplicationAction is not null && !_keymap.MatchesEditorAction(screenKey) &&
                _keymap.Matches("app.thinking.toggle", screenKey))
            {
                await dispatchApplicationAction("app.thinking.toggle");
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
        screen.AppendThemedStatusLine($"Tool output: {(_toolResultsExpanded ? "expanded" : "collapsed")}");
        if (_transcriptSearch.IsActive) _transcriptSearch.Update();
    }

    public async Task<string?> ReadLineAsync(Func<string, Task> dispatchApplicationAction,
        bool enableApplicationActions = true, bool allowEmptySubmit = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatchApplicationAction);
        _completionCycle = null;
        using var mode = TerminalMode.Enter(_screen);
        var input = EnsureInput();
        Render();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!input.TryRead(40, out var next)) continue;
            var isCompletionKey = next.Key is { } possibleCompletionKey &&
                _keymap.Matches("tui.input.tab", possibleCompletionKey);
            if (!isCompletionKey) _completionCycle = null;
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
            if (enableApplicationActions && next.Key is { } clearKey && _keymap.Matches("app.clear", clearKey))
            {
                var now = Environment.TickCount64;
                if (_lastClearAtMilliseconds is { } previous && now - previous < 500)
                {
                    ClearLine();
                    Console.WriteLine();
                    return null;
                }
                _lastClearAtMilliseconds = now;
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
                if (_screen is not { IsActive: true }) Console.WriteLine($"{Prompt}{submitted}");
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
                    _completionCycle = null;
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
                    if (_screen is not { IsActive: true } && !submitted.StartsWith("/", StringComparison.Ordinal))
                        Console.WriteLine($"{Prompt}{new string(submitted.Replace('\n', '↵').Select(c => char.IsControl(c) ? ' ' : c).ToArray())}");
                    _buffer.Clear();
                    return submitted;
                case EditorAction.Render: Render(); break;
            }
        }
    }

    internal Task MonitorMcpAuthorizationAsync(Task operation,
        Func<string, Task> dispatchApplicationAction, Action cancel) =>
        _authorizationInput.MonitorMcpAuthorizationAsync(operation, dispatchApplicationAction, cancel);

    public async Task<bool> HandleApplicationShortcutAsync(ConsoleKeyInfo key, Func<string, Task> dispatch,
        Action? beforeDispatch = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (_keymap.MatchesEditorAction(key)) return false;
        var action = _keymap.MatchIdleApplicationAction(key);
        if (action is null) return false;
        beforeDispatch?.Invoke();
        if (action == "app.tools.expand")
        {
            ToggleToolResults();
            return true;
        }
        await dispatch(action);
        return true;
    }

    private void CompleteInput(ConsoleKeyInfo key)
    {
        CompletionCycle cycle;
        if (_completionCycle is { } previous && previous.Matches(_buffer))
        {
            var direction = key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1;
            var selectedIndex = (previous.SelectedIndex + direction + previous.Candidates.Count) % previous.Candidates.Count;
            cycle = previous with { SelectedIndex = selectedIndex };
        }
        else
        {
            var matches = _completion.GetCompletions(_buffer);
            if (matches.Count == 0)
            {
                _completionCycle = null;
                return;
            }
            cycle = new(matches, key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? matches.Count - 1 : 0,
                _buffer.Text, _buffer.Cursor);
        }

        _completion.ApplySelected(_buffer, cycle.Candidates[cycle.SelectedIndex]);
        _completionCycle = cycle with { BufferText = _buffer.Text, Cursor = _buffer.Cursor };
    }

    private void ClearLine()
    {
        if (_screen is { IsActive: true })
        {
            _screen.SetEditor("", 0);
            _lastRenderedEditorText = "";
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
            var text = _buffer.Text;
            var suggestions = _completion.GetSlashCommandSuggestions(_buffer);
            if (suggestions.Count > 0 && text.StartsWith("/", StringComparison.Ordinal) &&
                !(_lastRenderedEditorText?.StartsWith("/", StringComparison.Ordinal) ?? false))
                _screen.SetEditor(text, _buffer.Cursor, _buffer.SelectionStart, _buffer.SelectionEnd, [], immediate: true);
            _screen.SetEditor(text, _buffer.Cursor, _buffer.SelectionStart, _buffer.SelectionEnd, suggestions);
            _lastRenderedEditorText = text;
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
