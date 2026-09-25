namespace PiSharp.Cli.Tui;

/// <summary>Normal-screen terminal editor. Leaves transcript in the terminal scrollback.</summary>
public sealed class TerminalEditor
{
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
    public TerminalEditor(Func<IReadOnlyList<string>>? commands = null, string? agentDirectory = null)
    {
        _completion = new(Environment.CurrentDirectory, commands);
        _keymap = new(agentDirectory);
        _buffer = new(_keymap);
    }
    private const string Prompt = "❯ ";
    private int _renderedRows;
    private int _cursorRow;

    public string Draft => _buffer.Text;
    public string Hotkeys => _keymap.FormatHotkeys();

    public void ReloadKeybindings() => _keymap.Reload();

    public void AttachScreen(TerminalScreen? screen)
    {
        _screen = screen;
        if (screen is not null)
        {
            screen.SetToolResultsExpanded(_toolResultsExpanded);
            screen.SetEditor(_buffer.Text, _buffer.Cursor);
        }
    }

    internal TerminalSelection<T>? ShowSelectionList<T>(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, string? selectedKey = null,
        IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items")
    {
        if (_screen is not { IsActive: true } screen) return null;
        _input ??= TerminalInput.OpenConsole();
        using var mode = TerminalMode.Enter(screen);
        return new TerminalOverlayHost(_input).Select(screen, title, options, selectedKey, scopedOptions,
            allLabel, scopedLabel, emptyMessage);
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
        var previous = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
            using var mode = TerminalMode.Enter(_screen);
            _input ??= TerminalInput.OpenConsole();
            _screen?.SetFooter(ActiveRunFooter);
            Render();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_input.TryRead(50, out var next))
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
            Console.TreatControlCAsInput = previous;
        }
    }

    /// <summary>Apply one active-run key event. Exposed separately for deterministic input tests.</summary>
    public async Task<bool> HandleActiveInputAsync(TerminalInputEvent next,
        Func<string, bool, CancellationToken, Task<bool>> queue, Func<IReadOnlyList<string>> clearQueue,
        Action abort, CancellationToken cancellationToken = default,
        Func<string, Task>? dispatchApplicationAction = null)
    {
        if (_searchingTranscript)
        {
            if (next.Key is { } searchKey && _keymap.Matches("app.tools.expand", searchKey))
            {
                ToggleToolResults();
                return true;
            }
            return HandleTranscriptSearchInput(next);
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

    public async Task<string?> ReadLineAsync(Func<string, Task> dispatchApplicationAction, bool enableApplicationActions = true)
    {
        ArgumentNullException.ThrowIfNull(dispatchApplicationAction);
        var previous = Console.TreatControlCAsInput;
        try
        {
            Console.TreatControlCAsInput = true;
            using var mode = TerminalMode.Enter(_screen);
            _input ??= TerminalInput.OpenConsole();
            Render();
            while (true)
            {
                var next = _input.Read();
                if (next.Key is null && next.Text is null)
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
                if (next.Key is { } tabKey && _keymap.Matches("tui.input.tab", tabKey))
                {
                    try
                    {
                        var matches = _completion.Complete(_buffer);
                        if (matches.Count == 0) _buffer.Handle(next.Key.Value);
                        else if (matches.Count > 1)
                        {
                            ClearLine();
                            Console.WriteLine();
                            Console.WriteLine(string.Join("  ", matches.Take(10).Select(match =>
                                new string(match.Select(c => char.IsControl(c) ? ' ' : c).ToArray()))) +
                                (matches.Count > 10 ? "  …" : ""));
                        }
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                    {
                        ClearLine();
                        Console.WriteLine($"\nCompletion unavailable: {error.Message}");
                    }
                    Render();
                    continue;
                }
                var action = next.Text is not null ? _buffer.InsertText(next.Text) : _buffer.Handle(next.Key!.Value);
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
                        Console.WriteLine($"{Prompt}{new string(submitted.Replace('\n', '↵').Select(c => char.IsControl(c) ? ' ' : c).ToArray())}");
                        _buffer.Clear();
                        return submitted;
                    case EditorAction.Render: Render(); break;
                }
            }
        }
        finally { Console.TreatControlCAsInput = previous; }
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
            _screen.SetEditor(_buffer.Text, _buffer.Cursor);
            return;
        }
        var width = Console.WindowWidth > 0 ? Console.WindowWidth : 80;
        var height = Console.WindowHeight > 0 ? Console.WindowHeight : 24;
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
