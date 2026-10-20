using PiSharp.Runtime.Extensions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

/// <summary>Edits drafts and routes idle and active-run input to application actions and terminal dialogs.</summary>
public sealed class TerminalEditor
{
    private sealed record CompletionCandidate(string DisplayText, string ReplacementText, bool Native,
        string? Description = null);
    private sealed record CompletionCycle(IReadOnlyList<CompletionCandidate> Candidates, int SelectedIndex,
        string BufferText, int Cursor)
    {
        public bool Matches(EditorBuffer buffer) => BufferText == buffer.Text && Cursor == buffer.Cursor;
    }

    private readonly EditorBuffer _buffer;
    private TerminalInput? _input;
    private readonly EditorCompletion _completion;
    private readonly EditorKeymap _keymap;
    private readonly Func<IReadOnlyList<ExtensionShortcutInfo>>? _getExtensionShortcuts;
    private readonly TerminalDialogs _dialogs;
    private readonly TerminalProgressDisplay _progress;
    private readonly TerminalTranscriptSearchInput _transcriptSearch;
    private readonly TerminalAuthorizationInput _authorizationInput;
    private readonly Queue<TerminalInputEvent> _extensionRewriteQueue = [];
    private readonly object _extensionRewriteQueueGate = new();
    private TerminalScreen? _screen;
    private Func<TerminalInputEvent, bool, TerminalInputInterception>? _extensionInputInterceptor;
    private Func<TerminalInputEvent, bool>? _extensionEditorInputHandler;
    private Func<string, int, CancellationToken,
        Task<IReadOnlyList<NativeExtensions.PiSharpExtensionAutocompleteSuggestion>>>? _extensionAutocompleteProvider;
    private CancellationTokenSource? _completionRequest;
    private Func<string, Task>? _extensionModalDispatchApplicationAction;
    private Func<ExtensionShortcutInfo, CancellationToken, Task>? _extensionModalDispatchShortcut;
    private int _activeInputLoops;
    private int _suspendedInputLoops;
    private string? _lastRenderedEditorText;
    private bool _toolResultsExpanded;
    private long? _lastClearAtMilliseconds;
    private CompletionCycle? _completionCycle;
    public TerminalEditor(Func<IReadOnlyList<string>>? commands = null, string? agentDirectory = null,
        Func<string>? getWorkingDirectory = null, bool initiallyExpanded = false,
        Func<string, string?>? describeCommand = null,
        Func<IReadOnlyList<ExtensionShortcutInfo>>? getExtensionShortcuts = null)
    {
        _completion = getWorkingDirectory is null
            ? new EditorCompletion(Environment.CurrentDirectory, commands, describeCommand)
            : new EditorCompletion(getWorkingDirectory, commands, describeCommand);
        _keymap = new(agentDirectory);
        _getExtensionShortcuts = getExtensionShortcuts;
        RefreshExtensionShortcuts();
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
        RefreshExtensionShortcuts();
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

    internal void AttachTerminalInput(TerminalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _input = input;
        _screen?.AttachTerminalInput(input);
    }

    internal void SetExtensionInputInterceptor(Func<TerminalInputEvent, bool, TerminalInputInterception>? interceptor) =>
        _extensionInputInterceptor = interceptor;

    internal void SetExtensionEditorInputHandler(Func<TerminalInputEvent, bool>? handler) =>
        _extensionEditorInputHandler = handler;

    internal void SetExtensionAutocompleteProvider(Func<string, int, CancellationToken,
        Task<IReadOnlyList<NativeExtensions.PiSharpExtensionAutocompleteSuggestion>>>? provider)
    {
        CancelCompletionRequest();
        _extensionAutocompleteProvider = provider;
    }

    internal void SetExtensionModalActionHandlers(Func<string, Task>? dispatchApplicationAction,
        Func<ExtensionShortcutInfo, CancellationToken, Task>? dispatchExtensionShortcut)
    {
        _extensionModalDispatchApplicationAction = dispatchApplicationAction;
        _extensionModalDispatchShortcut = dispatchExtensionShortcut;
    }

    internal bool ShouldRunExtensionModalInputPump => Volatile.Read(ref _activeInputLoops) == 0 ||
        Volatile.Read(ref _suspendedInputLoops) > 0;

    internal async Task RunExtensionModalInputPumpAsync(Func<bool> shouldContinue,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shouldContinue);
        if (!ShouldRunExtensionModalInputPump) return;
        using var inputLoop = EnterInputLoop();
        using var mode = Console.IsInputRedirected ? null : TerminalMode.Enter(_screen);
        var input = EnsureInput();
        Render();
        while (shouldContinue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadExtensionInput(input, 40, out var next))
            {
                _screen?.RefreshIfResized();
                continue;
            }
            if (InterceptExtensionInput(ref next)) continue;
            if (next.IsControl) continue;
            if (next.IsEndOfStream) return;
            if (next.Mouse is { } mouse)
            {
                _ = _screen?.HandleMouse(mouse);
                continue;
            }
            if (next.Key is { } extensionKey && _extensionModalDispatchShortcut is not null &&
                _keymap.MatchExtensionShortcut(extensionKey) is { } extensionShortcut)
            {
                using var suspended = SuspendInputLoop();
                await _extensionModalDispatchShortcut(extensionShortcut, cancellationToken);
                continue;
            }
            if (next.Key is { } applicationKey && _extensionModalDispatchApplicationAction is not null &&
                await HandleApplicationShortcutAsync(applicationKey, _extensionModalDispatchApplicationAction,
                    dispatchExtensionShortcut: _extensionModalDispatchShortcut,
                    cancellationToken: cancellationToken))
            {
                Render();
                continue;
            }
            if (HandleExtensionEditorInput(next))
            {
                Render();
                continue;
            }
            if (next.Key is null && next.Text is null) continue;
            if (next.Key is { } tabKey && _keymap.Matches("tui.input.tab", tabKey))
            {
                await CompleteInputAsync(tabKey, cancellationToken);
                Render();
                continue;
            }
            if (TerminalInput.TryGetText(next, out _))
            {
                string text;
                if (next.IsExtensionRewrite)
                {
                    _ = TerminalInput.TryGetText(next, out text);
                }
                else if (_extensionInputInterceptor is not null)
                {
                    _ = TerminalInput.TryGetText(next, out text);
                }
                else
                {
                    text = input.ReadAvailableText(next, out var following);
                    if (following is not null) input.PushBack(following);
                }
                _buffer.InsertText(text);
            }
            else if (next.Key is { } key)
            {
                _ = _buffer.Handle(key);
            }
            Render();
        }
    }

    internal object BeginExtensionEdit(string? prefill)
    {
        var state = _buffer.CaptureExtensionEditState();
        _completionCycle = null;
        _buffer.BeginExtensionEdit(prefill);
        Render();
        return state;
    }

    internal void EndExtensionEdit(object state)
    {
        _buffer.RestoreExtensionEditState(state);
        _completionCycle = null;
        Render();
    }

    internal bool HandleExtensionEditKey(ConsoleKeyInfo key)
    {
        var action = _buffer.Handle(key);
        if (action == EditorAction.Render) Render();
        return action != EditorAction.None;
    }

    internal bool IsHostActionKey(ConsoleKeyInfo key) => !_keymap.MatchesEditorAction(key) &&
        (_keymap.MatchIdleApplicationAction(key) is not null || _keymap.MatchExtensionShortcut(key) is not null);

    internal bool IsReservedExtensionInput(TerminalInputEvent input) => input.Key is { } key &&
        (_keymap.Matches("app.interrupt", key) || _keymap.Matches("app.clear", key) ||
            _buffer.Text.Length == 0 && _keymap.Matches("app.exit", key));

    private bool TryReadExtensionInput(TerminalInput input, int timeoutMilliseconds, out TerminalInputEvent value)
    {
        lock (_extensionRewriteQueueGate)
        {
            if (_extensionRewriteQueue.TryDequeue(out value!)) return true;
        }
        return input.TryRead(timeoutMilliseconds, out value);
    }

    private bool TryReadActiveExtensionInput(TerminalInput input, int timeoutMilliseconds,
        CancellationToken activityEnded, out TerminalInputEvent value)
    {
        if (activityEnded.IsCancellationRequested)
        {
            value = new(null, null);
            return false;
        }
        lock (_extensionRewriteQueueGate)
        {
            if (_extensionRewriteQueue.TryDequeue(out value!))
            {
                if (!activityEnded.IsCancellationRequested) return true;
                _extensionRewriteQueue.Enqueue(value);
                value = new(null, null);
                return false;
            }
        }
        return input.TryReadActive(timeoutMilliseconds, activityEnded, out value);
    }

    private bool HandleExtensionEditorInput(TerminalInputEvent input) =>
        _extensionEditorInputHandler?.Invoke(input) == true;

    private IDisposable EnterInputLoop()
    {
        Interlocked.Increment(ref _activeInputLoops);
        return new InputLoopLease(() => Interlocked.Decrement(ref _activeInputLoops));
    }

    private IDisposable SuspendInputLoop()
    {
        Interlocked.Increment(ref _suspendedInputLoops);
        return new InputLoopLease(() => Interlocked.Decrement(ref _suspendedInputLoops));
    }

    private async Task DispatchApplicationActionAsync(Func<string, Task> dispatch, string action)
    {
        using var suspended = SuspendInputLoop();
        await dispatch(action);
    }

    private bool InterceptExtensionInput(ref TerminalInputEvent input)
    {
        if (_extensionInputInterceptor is null || input.IsEndOfStream || input.IsControl)
            return false;
        var reserved = IsReservedExtensionInput(input);
        var result = _extensionInputInterceptor(input, reserved);
        if (result.Following is { Count: > 0 } following)
        {
            lock (_extensionRewriteQueueGate)
            {
                var existing = _extensionRewriteQueue.ToArray();
                _extensionRewriteQueue.Clear();
                foreach (var item in following) _extensionRewriteQueue.Enqueue(item);
                foreach (var item in existing) _extensionRewriteQueue.Enqueue(item);
            }
        }
        if (result.Consumed) return true;
        input = result.Input;
        return false;
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

    internal TerminalSettingsListSelection? ShowSettingsList(string title,
        IReadOnlyList<TerminalSettingsListItem> items, string? selectedKey, string scopeLabel,
        bool canChangeScope, CancellationToken cancellationToken = default) =>
        _dialogs.ShowSettingsList(title, items, selectedKey, scopeLabel, canChangeScope, cancellationToken);

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
        CancelCompletionRequest();
        _completionCycle = null;
        _buffer.SetText(text);
    }

    public void InsertTextAtCursor(string text)
    {
        CancelCompletionRequest();
        _completionCycle = null;
        _buffer.InsertText(text);
        Render();
    }

    internal void SetDraft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        CancelCompletionRequest();
        _completionCycle = null;
        _buffer.SetText(text);
        Render();
    }

    internal EditorBuffer CreateExtensionDialogBuffer() => new(_keymap);

    /// <summary>Return queued messages to the editor without discarding a draft typed during the run.</summary>
    public void RestorePending(IEnumerable<string> messages)
    {
        CancelCompletionRequest();
        _completionCycle = null;
        var restored = messages.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        if (!string.IsNullOrWhiteSpace(_buffer.Text)) restored.Add(_buffer.Text);
        _buffer.SetText(string.Join("\n\n", restored));
    }

    /// <summary>Read active-run controls while provider output streams. Enter steers, Alt+Enter
    /// follows up, Alt+Up restores queued input, and Escape aborts after restoring queued input.</summary>
    public async Task MonitorRunAsync(Func<string, bool, CancellationToken, Task<bool>> queue,
        Func<IReadOnlyList<string>> clearQueue, Action abort, CancellationToken cancellationToken,
        Func<string, Task>? dispatchApplicationAction = null,
        Func<ExtensionShortcutInfo, CancellationToken, Task>? dispatchExtensionShortcut = null)
    {
        using var inputLoop = EnterInputLoop();
        try
        {
            using var mode = TerminalMode.Enter(_screen);
            var input = EnsureInput();
            Render();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!TryReadActiveExtensionInput(input, 50, cancellationToken, out var next))
                {
                    _screen?.RefreshIfResized();
                    await Task.Delay(10, CancellationToken.None);
                    continue;
                }
                if (!await HandleActiveInputAsync(next, queue, clearQueue, abort, cancellationToken, dispatchApplicationAction,
                    dispatchExtensionShortcut)) break;
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
        Func<string, Task>? dispatchApplicationAction = null,
        Func<ExtensionShortcutInfo, CancellationToken, Task>? dispatchExtensionShortcut = null)
    {
        var isCompletionKey = next.Key is { } possibleCompletionKey &&
            _keymap.Matches("tui.input.tab", possibleCompletionKey);
        if (!isCompletionKey)
        {
            _completionCycle = null;
            CancelCompletionRequest();
        }
        if (next.IsEndOfStream) return false;
        if (next.IsControl) return true;
        if (InterceptExtensionInput(ref next)) return true;
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
                await DispatchApplicationActionAsync(dispatchApplicationAction, "app.message.copy");
            return true;
        }
        if (next.Key is { } extensionKey && dispatchExtensionShortcut is not null &&
            _keymap.MatchExtensionShortcut(extensionKey) is { } extensionShortcut)
        {
            using var suspended = SuspendInputLoop();
            await dispatchExtensionShortcut(extensionShortcut, cancellationToken);
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
                await CompleteInputAsync(completionKey, cancellationToken);
                Render();
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
                await DispatchApplicationActionAsync(dispatchApplicationAction, "app.thinking.toggle");
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
                await DispatchApplicationActionAsync(dispatchApplicationAction, "app.message.copy");
                return true;
            }
            if (_keymap.Matches("app.clipboard.pasteImage", clipboardKey))
            {
                await DispatchApplicationActionAsync(dispatchApplicationAction, "app.clipboard.pasteImage");
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
        if (HandleExtensionEditorInput(next)) return true;
        if (next.Key is null && next.Text is null) return true;
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
        CancellationToken cancellationToken = default,
        Func<ExtensionShortcutInfo, CancellationToken, Task>? dispatchExtensionShortcut = null)
    {
        ArgumentNullException.ThrowIfNull(dispatchApplicationAction);
        _completionCycle = null;
        using var inputLoop = EnterInputLoop();
        using var mode = TerminalMode.Enter(_screen);
        var input = EnsureInput();
        Render();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadExtensionInput(input, 40, out var next))
            {
                _screen?.RefreshIfResized();
                continue;
            }
            if (InterceptExtensionInput(ref next)) continue;
            if (next.IsControl) continue;
            var isCompletionKey = next.Key is { } possibleCompletionKey &&
                _keymap.Matches("tui.input.tab", possibleCompletionKey);
            if (!isCompletionKey)
            {
                _completionCycle = null;
                CancelCompletionRequest();
            }
            if (next.Mouse is { } mouse)
            {
                var result = _screen?.HandleMouse(mouse) ?? default;
                if (result.EditorCursorOffset is { } cursor)
                {
                    _buffer.SetCursor(cursor);
                    Render();
                }
                if (result.Copy)
                    await DispatchApplicationActionAsync(dispatchApplicationAction, "app.message.copy");
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
                    await DispatchApplicationActionAsync(dispatchApplicationAction, externalAction);
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
                await HandleApplicationShortcutAsync(applicationKey, dispatchApplicationAction, ClearLine,
                    dispatchExtensionShortcut, cancellationToken))
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
            if (HandleExtensionEditorInput(next))
            {
                Render();
                continue;
            }
            if (next.Key is null && next.Text is null) continue;
            if (next.Key is { } tabKey && _keymap.Matches("tui.input.tab", tabKey))
            {
                try
                {
                    await CompleteInputAsync(tabKey, cancellationToken);
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
                string text;
                TerminalInputEvent? following = null;
                if (next.IsExtensionRewrite)
                {
                    _ = TerminalInput.TryGetText(next, out text);
                }
                else if (_extensionInputInterceptor is not null)
                {
                    _ = TerminalInput.TryGetText(next, out text);
                }
                else
                {
                    text = input.ReadAvailableText(next, out following);
                }
                action = _buffer.InsertText(text);
                if (!next.IsExtensionRewrite && following?.Key is { } bufferedSubmitKey &&
                    _keymap.Matches("tui.input.submit", bufferedSubmitKey))
                {
                    next = following;
                    action = EditorAction.Submit;
                }
                else
                {
                    if (!next.IsExtensionRewrite && following is not null) input.PushBack(following);
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
        Action? beforeDispatch = null,
        Func<ExtensionShortcutInfo, CancellationToken, Task>? dispatchExtensionShortcut = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (dispatchExtensionShortcut is not null && _keymap.MatchExtensionShortcut(key) is { } extensionShortcut)
        {
            using var suspended = SuspendInputLoop();
            await dispatchExtensionShortcut(extensionShortcut, cancellationToken);
            return true;
        }
        if (_keymap.MatchesEditorAction(key) &&
            !(_keymap.Matches("app.thinking.cycle", key) && _completionCycle is null)) return false;
        var action = _keymap.MatchIdleApplicationAction(key);
        if (action is null) return false;
        beforeDispatch?.Invoke();
        if (action == "app.tools.expand")
        {
            ToggleToolResults();
            return true;
        }
        using (SuspendInputLoop()) await dispatch(action);
        return true;
    }

    private void RefreshExtensionShortcuts()
    {
        _keymap.SetExtensionShortcuts(_getExtensionShortcuts?.Invoke() ?? []);
        foreach (var diagnostic in _keymap.ShortcutDiagnostics)
            Console.Error.WriteLine($"Extension warning ({diagnostic.SourcePath}): {diagnostic.Message}");
    }

    private async Task CompleteInputAsync(ConsoleKeyInfo key, CancellationToken cancellationToken)
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
            CancelCompletionRequest();
            var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _completionRequest = request;
            try
            {
                var originalText = _buffer.Text;
                var originalCursor = _buffer.Cursor;
                var descriptions = _completion.GetSlashCommandSuggestions(_buffer)
                    .ToDictionary(suggestion => suggestion.Command, suggestion => suggestion.Description,
                        StringComparer.OrdinalIgnoreCase);
                var candidates = _completion.GetCompletions(_buffer)
                    .Select(value => new CompletionCandidate(value, value, Native: true,
                        descriptions.GetValueOrDefault(value))).ToList();
                if (_extensionAutocompleteProvider is not null)
                {
                    var extensions = await _extensionAutocompleteProvider(originalText, originalCursor, request.Token)
                        .ConfigureAwait(false);
                    request.Token.ThrowIfCancellationRequested();
                    if (!string.Equals(_buffer.Text, originalText, StringComparison.Ordinal) ||
                        _buffer.Cursor != originalCursor)
                    {
                        _completionCycle = null;
                        return;
                    }
                    var seen = new HashSet<string>(candidates.Select(candidate => candidate.ReplacementText),
                        StringComparer.Ordinal);
                    foreach (var suggestion in extensions)
                    {
                        if (string.IsNullOrEmpty(suggestion.ReplacementText) ||
                            string.IsNullOrEmpty(suggestion.DisplayText) || !seen.Add(suggestion.ReplacementText)) continue;
                        candidates.Add(new(suggestion.DisplayText, suggestion.ReplacementText, Native: false,
                            suggestion.Description));
                        if (candidates.Count == 100) break;
                    }
                }
                if (candidates.Count == 0)
                {
                    _completionCycle = null;
                    return;
                }
                cycle = new(candidates, key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? candidates.Count - 1 : 0,
                    originalText, originalCursor);
            }
            finally
            {
                Interlocked.CompareExchange(ref _completionRequest, null, request);
                request.Dispose();
            }
        }

        var selected = cycle.Candidates[cycle.SelectedIndex];
        if (selected.Native)
            _completion.ApplySelected(_buffer, selected.ReplacementText);
        else
        {
            var start = _buffer.Cursor;
            while (start > 0 && !char.IsWhiteSpace(_buffer.Text[start - 1])) start--;
            _buffer.Replace(start, _buffer.Cursor - start, selected.ReplacementText);
        }
        _completionCycle = cycle with { BufferText = _buffer.Text, Cursor = _buffer.Cursor };
    }

    private void CancelCompletionRequest()
    {
        var request = Interlocked.Exchange(ref _completionRequest, null);
        if (request is null) return;
        try { request.Cancel(); }
        catch (Exception error)
        {
            try { Console.Error.WriteLine($"Extension autocomplete cancellation failed: {TerminalSafeText.Normalize(error.Message)}"); }
            catch (Exception) { }
        }
        finally { request.Dispose(); }
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

    private sealed class InputLoopLease(Action release) : IDisposable
    {
        private int _active = 1;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) != 0) release();
        }
    }

    private void Render()
    {
        if (_screen is { IsActive: true })
        {
            var text = _buffer.Text;
            var suggestions = _completion.GetSlashCommandSuggestions(_buffer);
            if (_completionCycle is { } completionCycle && completionCycle.Matches(_buffer))
                suggestions = completionCycle.Candidates.Select((candidate, index) =>
                    new EditorCompletionSuggestion(
                        $"{(index == completionCycle.SelectedIndex ? "❯ " : "  ")}{candidate.DisplayText}",
                        candidate.Description)).ToArray();
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
