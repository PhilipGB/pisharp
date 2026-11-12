using PiSharp.Core;
using PiSharp.Runtime.Extensions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

/// <summary>Edits drafts and routes idle and active-run input to application actions and terminal dialogs.</summary>
public sealed class TerminalEditor
{
    internal static readonly string[] ActiveApplicationActions =
    [
        "app.thinking.cycle", "app.thinking.toggle", "app.model.cycleForward",
        "app.model.cycleBackward", "app.model.select", "app.session.tree"
    ];

    private sealed record CompletionCandidate(string DisplayText, string ReplacementText, bool Native,
        string? Description = null, bool IsDirectory = false, EditorCompletionItem? Item = null,
        EditorCompletionKind Kind = EditorCompletionKind.Slash, string Prefix = "");
    private sealed record CompletionCycle(IReadOnlyList<CompletionCandidate> Candidates, int SelectedIndex,
        bool Force, string BufferText, int Cursor)
    {
        public bool Matches(EditorBufferSnapshot snapshot) => BufferText == snapshot.Text && Cursor == snapshot.Cursor;
    }

    private readonly EditorBuffer _buffer;
    private TerminalInput? _input;
    private readonly EditorAutocomplete _autocomplete;
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
    private long? _lastEscapeAtMilliseconds;
    private Func<string>? _getDoubleEscapeAction;
    private Func<long> _clockMilliseconds = static () => Environment.TickCount64;
    private CompletionCycle? _completionCycle;
    private bool _suppressAutoCompletion;
    private string? _pendingBashSubmission;
    public TerminalEditor(Func<IReadOnlyList<string>>? commands = null, string? agentDirectory = null,
        Func<string>? getWorkingDirectory = null, bool initiallyExpanded = false,
        Func<string, string?>? describeCommand = null,
        Func<IReadOnlyList<ExtensionShortcutInfo>>? getExtensionShortcuts = null,
        Func<IReadOnlyList<EditorModelOption>>? getModels = null,
        Func<IReadOnlyList<string>>? getThinkingLevels = null,
        Func<IReadOnlyList<EditorLoginProvider>>? getLoginProviders = null)
    {
        _autocomplete = getWorkingDirectory is null
            ? new EditorAutocomplete(() => Environment.CurrentDirectory, commands, describeCommand, getModels,
                getThinkingLevels, getLoginProviders)
            : new EditorAutocomplete(getWorkingDirectory, commands, describeCommand, getModels,
                getThinkingLevels, getLoginProviders);
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
    private int _normalEditorScrollOffset;

    public string Draft => WithEditorState(() => _buffer.CaptureSnapshot().Text);
    public string ExpandedDraft => WithEditorState(() => _buffer.ExpandedText);
    internal EditorBufferSnapshot CaptureDraftState() => WithEditorState(_buffer.CaptureSnapshot);
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
        _screen?.SetTranscriptBottomKeyLabel(_keymap.GetDisplayKeys("tui.altScreen.bottom"));
    }

    internal void SetDoubleEscapeActionProvider(Func<string>? provider) => _getDoubleEscapeAction = provider;

    internal void SetDoublePressClock(Func<long> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clockMilliseconds = clock;
    }

    public void AttachScreen(TerminalScreen? screen)
    {
        _screen = screen;
        if (screen is not null)
        {
            screen.SetToolResultsExpanded(_toolResultsExpanded);
            screen.SetToolExpandKeyLabel(_keymap.GetDisplayKeys("app.tools.expand"));
            screen.SetTranscriptBottomKeyLabel(_keymap.GetDisplayKeys("tui.altScreen.bottom"));
            var snapshot = WithEditorState(_buffer.CaptureSnapshot);
            screen.SetEditor(snapshot.Text, snapshot.Cursor, snapshot.SelectionStart, snapshot.SelectionEnd);
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
        var pendingRequest = WithEditorState(() =>
        {
            _completionCycle = null;
            var pending = Interlocked.Exchange(ref _completionRequest, null);
            _extensionAutocompleteProvider = provider;
            return pending;
        });
        CancelCompletionRequest(pendingRequest);
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
            UpdateEditorGeometry();
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
                var result = _screen?.HandleMouse(mouse) ?? default;
                if (result.Copy)
                {
                    var copied = _screen is not null && await _screen.CopySelectionToClipboardAsync();
                    if (!copied && _extensionModalDispatchApplicationAction is not null)
                        await _extensionModalDispatchApplicationAction("app.message.copy");
                }
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
                var action = WithEditorState(() => _buffer.IsJumpModeActive && !next.IsPaste
                    ? _buffer.HandleJumpText(text)
                    : _buffer.InsertText(text));
                if (action == EditorAction.Render) Render();
            }
            else if (next.Key is { } key)
            {
                WithEditorState(() => _buffer.Handle(key));
            }
            Render();
        }
    }

    internal object BeginExtensionEdit(string? prefill)
    {
        var state = WithEditorState(() =>
        {
            _completionCycle = null;
            var captured = _buffer.CaptureExtensionEditState();
            _buffer.BeginExtensionEdit(prefill);
            return captured;
        });
        Render();
        return state;
    }

    internal void EndExtensionEdit(object state)
    {
        WithEditorState(() =>
        {
            _buffer.RestoreExtensionEditState(state);
            _completionCycle = null;
        });
        Render();
    }

    internal bool HandleExtensionEditKey(ConsoleKeyInfo key)
    {
        UpdateEditorGeometry();
        var action = WithEditorState(() => _buffer.Handle(key));
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
        Func<TerminalSelectionList<T>, TerminalInputEvent, bool>? handleInput = null,
        string saveAction = "app.models.save") =>
        _dialogs.ShowSelectionList(title, options, selectedKey, scopedOptions, allLabel, scopedLabel, emptyMessage,
            actionHint, handleInput, saveAction);

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
        CancellationToken cancellationToken = default,
        Func<TerminalSelectionList<T>, Task>? onIdle = null,
        string saveAction = "app.models.save") =>
        _dialogs.ShowSelectionListAsync(title, options, selectedKey, scopedOptions, allLabel, scopedLabel,
            emptyMessage, actionHint, handleInput, cancellationToken, onIdle, saveAction);

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

    /// <summary>Shows the session tree navigator; returns the selected entry or null when cancelled.</summary>
    internal async Task<ConversationNode?> ShowTreeSelectorAsync(
        Func<IReadOnlyList<ConversationNode>> freshEntries,
        Func<string, string?, Task<bool>> onSaveLabelAsync,
        Func<string?, Task> onCopyAsync,
        string? currentLeafId, string? initialSelectedId, TerminalTreeFilterMode initialFilterMode)
    {
        if (_screen is not { IsActive: true } screen) return null;
        var input = EnsureInput();
        var selector = new TerminalTreeSelector(freshEntries, onSaveLabelAsync, onCopyAsync, _keymap);
        return await selector.ShowAsync(screen, input, currentLeafId, initialSelectedId, initialFilterMode)
            .ConfigureAwait(false);
    }

    internal void ShowInlineSelectionPanel<T>(string title, IReadOnlyList<TerminalSelectionOption<T>> options,
        IReadOnlyList<string>? header = null, string footer = "↑↓ move • enter select • escape/ctrl+c close",
        int selectedIndex = 0) =>
        _dialogs.ShowInlineSelectionPanel(title, options, header, footer, selectedIndex);

    internal Task<IReadOnlyList<string>?> PromptSequenceAsync(string title,
        IReadOnlyList<(string Message, string? Placeholder)> prompts, bool preservePanelAfterSubmit = false,
        bool preservePanelOnCancel = false, CancellationToken cancellationToken = default,
        bool extensionDialog = false, Func<string, Task>? dispatchApplicationAction = null,
        IReadOnlyList<string>? initialValues = null, IReadOnlyList<bool>? maskedPrompts = null) =>
        _dialogs.PromptSequenceAsync(title, prompts, preservePanelAfterSubmit, preservePanelOnCancel, cancellationToken,
            extensionDialog, dispatchApplicationAction, initialValues, maskedPrompts);

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
        var pendingRequest = WithEditorState(() =>
        {
            _completionCycle = null;
            var pending = Interlocked.Exchange(ref _completionRequest, null);
            _buffer.SetText(text);
            return pending;
        });
        CancelCompletionRequest(pendingRequest);
    }

    public void InsertTextAtCursor(string text)
    {
        var pendingRequest = WithEditorState(() =>
        {
            _completionCycle = null;
            var pending = Interlocked.Exchange(ref _completionRequest, null);
            _buffer.InsertText(text);
            return pending;
        });
        CancelCompletionRequest(pendingRequest);
        Render();
    }

    internal void SetDraft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var pendingRequest = WithEditorState(() =>
        {
            _completionCycle = null;
            var pending = Interlocked.Exchange(ref _completionRequest, null);
            _buffer.SetText(text);
            return pending;
        });
        CancelCompletionRequest(pendingRequest);
        Render();
    }

    internal EditorBuffer CreateExtensionDialogBuffer() => new(_keymap);

    /// <summary>Return queued messages to the editor without discarding a draft typed during the run.</summary>
    public void RestorePending(IEnumerable<string> messages)
    {
        var pendingRequest = WithEditorState(() =>
        {
            _completionCycle = null;
            var pending = Interlocked.Exchange(ref _completionRequest, null);
            var restored = messages.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (!string.IsNullOrWhiteSpace(_buffer.Text)) restored.Add(_buffer.Text);
            _buffer.SetText(string.Join("\n\n", restored));
            return pending;
        });
        CancelCompletionRequest(pendingRequest);
    }

    /// <summary>Apply idle-run queue controls. Enter-submit behaviour is returned through
    /// <paramref name="submitted"/>; handled non-submit keys return true with a null submission.</summary>
    internal bool HandleIdleQueueKey(ConsoleKeyInfo key, Func<IReadOnlyList<string>>? drainQueuedMessages,
        out string? submitted)
    {
        submitted = null;
        if (_keymap.Matches("app.message.dequeue", key))
        {
            var pending = drainQueuedMessages?.Invoke() ?? [];
            RestorePending(pending);
            ShowQueueStatus(pending.Count);
            return true;
        }
        if (!_keymap.Matches("app.message.followUp", key)) return false;
        // Pinned Pi treats Alt+Enter as a normal submit while no run is active.
        string? accepted = null;
        WithEditorState(() =>
        {
            if (!_buffer.TrySubmit(out var value) || string.IsNullOrWhiteSpace(value)) return false;
            accepted = value;
            _buffer.Clear();
            return true;
        });
        submitted = accepted;
        return true;
    }

    internal void ShowQueueStatus(int restoredCount) => ShowStatusMessage(QueueRestoreStatus(restoredCount));

    internal static string QueueRestoreStatus(int restoredCount) => restoredCount == 0
        ? "No queued messages to restore"
        : $"Restored {restoredCount} queued message{(restoredCount == 1 ? "" : "s")} to editor";

    internal void ShowStatusMessage(string message)
    {
        if (_screen is { IsActive: true } screen) screen.AppendThemedStatusLine(message);
        else Console.WriteLine(message);
        Render();
    }

    /// <summary>Read active-run controls while provider output streams. Enter steers, Alt+Enter
    /// follows up, Alt+Up restores queued input, and Escape aborts after restoring queued input.</summary>
    public async Task MonitorRunAsync(Func<string, bool, CancellationToken, Task<bool>> queue,
        Func<IReadOnlyList<string>> clearQueue, Action abort, CancellationToken cancellationToken,
        Func<string, Task>? dispatchApplicationAction = null,
        Func<ExtensionShortcutInfo, CancellationToken, Task>? dispatchExtensionShortcut = null,
        Action? requestShutdown = null)
    {
        using var inputLoop = EnterInputLoop();
        try
        {
            using var mode = TerminalMode.Enter(_screen);
            var input = EnsureInput();
            Render();
            while (!cancellationToken.IsCancellationRequested)
            {
                UpdateEditorGeometry();
                if (!TryReadActiveExtensionInput(input, 50, cancellationToken, out var next))
                {
                    _screen?.RefreshIfResized();
                    await Task.Delay(10, CancellationToken.None);
                    continue;
                }
                if (!await HandleActiveInputAsync(next, queue, clearQueue, abort, cancellationToken, dispatchApplicationAction,
                    dispatchExtensionShortcut, requestShutdown)) break;
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
        Func<ExtensionShortcutInfo, CancellationToken, Task>? dispatchExtensionShortcut = null,
        Action? requestShutdown = null)
    {
        var isCompletionKey = next.Key is { } possibleCompletionKey &&
            _keymap.Matches("tui.input.tab", possibleCompletionKey);
        var isInterruptKey = next.Key is { } possibleInterruptKey &&
            _keymap.Matches("app.interrupt", possibleInterruptKey);
        var hadAutocomplete = WithEditorState(() => _completionCycle is not null);
        var completionForce = WithEditorState(() => _completionCycle is { Force: true });
        if (next.Key is { } activeAutocompleteKey &&
            WithEditorState(() => _completionCycle is { } cycle && cycle.Matches(_buffer.CaptureSnapshot())))
        {
            if (_keymap.Matches("tui.select.up", activeAutocompleteKey))
            {
                MoveCompletionSelection(-1);
                Render();
                return true;
            }
            if (_keymap.Matches("tui.select.down", activeAutocompleteKey))
            {
                MoveCompletionSelection(1);
                Render();
                return true;
            }
            if (_keymap.Matches("tui.select.pageUp", activeAutocompleteKey))
            {
                MoveCompletionSelection(-5);
                Render();
                return true;
            }
            if (_keymap.Matches("tui.select.pageDown", activeAutocompleteKey))
            {
                MoveCompletionSelection(5);
                Render();
                return true;
            }
            if (_keymap.Matches("tui.select.confirm", activeAutocompleteKey))
            {
                var kind = WithEditorState(() =>
                {
                    if (_completionCycle is not { } current) return (EditorCompletionKind?)null;
                    var selected = current.Candidates[current.SelectedIndex];
                    var selectedKind = selected.Kind;
                    ApplyCompletionCycleUnsafe(current);
                    return selectedKind;
                });
                Render();
                if (kind is EditorCompletionKind.Path or EditorCompletionKind.Argument) return true;
            }
        }
        if (!isCompletionKey && !isInterruptKey)
        {
            WithEditorState(() => { _completionCycle = null; });
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
                WithEditorState(_buffer.ClearSelection);
                Render();
            }
            if (result.EditorCursorOffset is { } cursor)
            {
                WithEditorState(() => _buffer.SetCursor(cursor));
                Render();
            }
            if (result.Copy)
            {
                var copied = _screen is not null && await _screen.CopySelectionToClipboardAsync();
                if (!copied && dispatchApplicationAction is not null)
                    await DispatchApplicationActionAsync(dispatchApplicationAction, "app.message.copy");
            }
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
                WithEditorState(() => { _completionCycle = null; });
                Console.Error.WriteLine($"Completion unavailable: {error.Message}");
            }
            return true;
        }
        if (isInterruptKey)
        {
            if (WithEditorState(() => _completionCycle is not null))
            {
                WithEditorState(() => { _completionCycle = null; });
                CancelCompletionRequest();
                return true;
            }
            CancelCompletionRequest();
            WithEditorState(() => { _completionCycle = null; });
            RestorePending(clearQueue());
            abort();
            return false;
        }
        if (next.Key is { } exitKey && _keymap.Matches("app.exit", exitKey) &&
            WithEditorState(() => _buffer.Text.Length == 0))
        {
            requestShutdown?.Invoke();
            return false;
        }
        if (next.Key is { } clearKey && _keymap.Matches("app.clear", clearKey))
        {
            if (RegisterClearPress())
            {
                requestShutdown?.Invoke();
                return false;
            }
            WithEditorState(_buffer.Clear);
            return true;
        }
        if (next.Key is { } dequeueKey && _keymap.Matches("app.message.dequeue", dequeueKey))
        {
            var pending = clearQueue();
            RestorePending(pending);
            ShowQueueStatus(pending.Count);
            return true;
        }
        if (next.Key is { } screenKey && _screen is { IsActive: true } screen)
        {
            if (_keymap.Matches("tui.altScreen.search", screenKey))
            {
                _transcriptSearch.Start();
                return true;
            }
            if (dispatchApplicationAction is not null && !_keymap.MatchesEditorAction(screenKey))
            {
                foreach (var action in ActiveApplicationActions)
                {
                    if (!_keymap.Matches(action, screenKey)) continue;
                    await DispatchApplicationActionAsync(dispatchApplicationAction, action);
                    return true;
                }
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
            if (_keymap.Matches("tui.altScreen.previousPrompt", screenKey))
            {
                screen.ScrollToPreviousPrompt();
                return true;
            }
            if (_keymap.Matches("tui.altScreen.nextPrompt", screenKey))
            {
                screen.ScrollToNextPrompt();
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
        if (next.Key is { } clipboardKey && dispatchApplicationAction is not null)
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
                WithEditorState(() => _buffer.Handle(inputKey));
                return true;
            }
            var followUp = _keymap.Matches("app.message.followUp", inputKey);
            if (followUp || _keymap.Matches("tui.input.submit", inputKey))
            {
                var text = string.Empty;
                var submitted = WithEditorState(() =>
                {
                    if (!_buffer.TrySubmit(out var value)) return false;
                    text = value;
                    _buffer.Clear();
                    return true;
                });
                if (!submitted) return true;
                if (!await queue(text, followUp, cancellationToken)) RestorePending([text]);
                return true;
            }
        }
        if (HandleExtensionEditorInput(next)) return true;
        if (next.Key is null && next.Text is null) return true;
        var activeAction = WithEditorState(() =>
        {
            if (next.Text is not null)
            {
                if (_buffer.IsJumpModeActive && !next.IsPaste) return _buffer.HandleJumpText(next.Text);
                if (next.IsPaste) return _buffer.InsertPaste(next.Text);
                return _buffer.InsertText(next.Text);
            }
            return _buffer.Handle(next.Key!.Value);
        });
        if (next.Text is not null)
        {
            if (!next.IsPaste) RefreshAutocomplete(completionForce);
        }
        else if (next.Key is { } activeHandledKey &&
                 (IsCompletionRefreshKey(activeHandledKey) ||
                  hadAutocomplete && IsCursorMovementKey(activeHandledKey)))
        {
            WithEditorState(() => _suppressAutoCompletion = false);
            if (activeAction == EditorAction.Render) RefreshAutocomplete(completionForce);
        }
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
        Func<ExtensionShortcutInfo, CancellationToken, Task>? dispatchExtensionShortcut = null,
        Func<IReadOnlyList<string>>? drainQueuedMessages = null)
    {
        ArgumentNullException.ThrowIfNull(dispatchApplicationAction);
        if (Interlocked.Exchange(ref _pendingBashSubmission, null) is { } pendingBashSubmission)
            return pendingBashSubmission;
        WithEditorState(() => { _completionCycle = null; });
        using var inputLoop = EnterInputLoop();
        using var mode = TerminalMode.Enter(_screen);
        var input = EnsureInput();
        Render();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateEditorGeometry();
            if (!TryReadExtensionInput(input, 40, out var next))
            {
                _screen?.RefreshIfResized();
                continue;
            }
            if (InterceptExtensionInput(ref next)) continue;
            if (next.IsControl) continue;
            if (!next.IsPaste && TerminalInput.TryGetText(next, out _))
                WithEditorState(() => _suppressAutoCompletion = false);
            var isCompletionKey = next.Key is { } possibleCompletionKey &&
                _keymap.Matches("tui.input.tab", possibleCompletionKey);
            var isInterruptKey = next.Key is { } possibleInterruptKey &&
                _keymap.Matches("app.interrupt", possibleInterruptKey);
            var hadAutocomplete = WithEditorState(() => _completionCycle is not null);
            var completionForce = WithEditorState(() => _completionCycle is { Force: true });
            var autocompleteKey = next.Key;
            var autocompleteActive = autocompleteKey is { } candidateKey &&
                WithEditorState(() => _completionCycle is { } cycle && cycle.Matches(_buffer.CaptureSnapshot())) &&
                (_keymap.Matches("tui.select.up", candidateKey) ||
                 _keymap.Matches("tui.select.down", candidateKey) ||
                 _keymap.Matches("tui.select.pageUp", candidateKey) ||
                 _keymap.Matches("tui.select.pageDown", candidateKey) ||
                 _keymap.Matches("tui.select.confirm", candidateKey));
            if (autocompleteActive)
            {
                if (autocompleteKey is { } selectionKey && _keymap.Matches("tui.select.up", selectionKey))
                {
                    MoveCompletionSelection(-1);
                    Render();
                    continue;
                }
                if (autocompleteKey is { } downKey && _keymap.Matches("tui.select.down", downKey))
                {
                    MoveCompletionSelection(1);
                    Render();
                    continue;
                }
                if (autocompleteKey is { } pageUpKey && _keymap.Matches("tui.select.pageUp", pageUpKey))
                {
                    MoveCompletionSelection(-5);
                    Render();
                    continue;
                }
                if (autocompleteKey is { } pageDownKey && _keymap.Matches("tui.select.pageDown", pageDownKey))
                {
                    MoveCompletionSelection(5);
                    Render();
                    continue;
                }
                var completionKind = WithEditorState(() =>
                {
                    if (_completionCycle is not { } cycle) return (EditorCompletionKind?)null;
                    var selected = cycle.Candidates[cycle.SelectedIndex];
                    var kind = selected.Kind;
                    ApplyCompletionCycleUnsafe(cycle);
                    return kind;
                });
                Render();
                if (completionKind is EditorCompletionKind.Path or EditorCompletionKind.Argument) continue;
            }
            if (!isCompletionKey && !isInterruptKey)
            {
                WithEditorState(() => { _completionCycle = null; });
                CancelCompletionRequest();
            }
            if (next.Mouse is { } mouse)
            {
                var result = _screen?.HandleMouse(mouse) ?? default;
                if (result.EditorCursorOffset is { } cursor)
                {
                    WithEditorState(() => _buffer.SetCursor(cursor));
                    Render();
                }
                if (result.Copy)
                {
                    var copied = _screen is not null && await _screen.CopySelectionToClipboardAsync();
                    if (!copied)
                        await DispatchApplicationActionAsync(dispatchApplicationAction, "app.message.copy");
                }
                continue;
            }
            if (next.IsEndOfStream)
            {
                ExitInputLoop();
                return null;
            }
            if (HandleIdleTranscriptInput(next)) continue;
            if (enableApplicationActions && next.Key is { } interruptKey &&
                _keymap.Matches("app.interrupt", interruptKey))
            {
                if (await HandleIdleEscapeAsync(dispatchApplicationAction)) Render();
                continue;
            }
            if (enableApplicationActions && next.Key is { } clearKey && _keymap.Matches("app.clear", clearKey))
            {
                if (RegisterClearPress())
                {
                    ExitInputLoop();
                    return null;
                }
            }
            if (enableApplicationActions && next.Key is { } queueKey &&
                HandleIdleQueueKey(queueKey, drainQueuedMessages, out var queuedSubmission))
            {
                if (queuedSubmission is { } submittedFollowUp)
                {
                    ClearLine();
                    if (_screen is not { IsActive: true } && !submittedFollowUp.StartsWith("/", StringComparison.Ordinal))
                        Console.WriteLine($"{Prompt}{new string(submittedFollowUp.Replace('\n', '↵')
                            .Select(character => char.IsControl(character) ? ' ' : character).ToArray())}");
                    return submittedFollowUp;
                }
                Render();
                continue;
            }
            if (enableApplicationActions && next.Key is { } externalEditorKey &&
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
                _keymap.Matches("tui.input.submit", submitKey))
            {
                var submitted = WithEditorState(() =>
                {
                    if (!string.IsNullOrWhiteSpace(_buffer.Text)) return null;
                    var value = _buffer.Text;
                    _buffer.Clear();
                    return value;
                });
                if (submitted is not null)
                {
                    ClearLine();
                    if (_screen is not { IsActive: true }) Console.WriteLine($"{Prompt}{submitted}");
                    return submitted;
                }
            }
            if (HandleExtensionEditorInput(next))
            {
                Render();
                continue;
            }
            if (next.Key is null && next.Text is null) continue;
            if (next.Key is { } tabKey && _keymap.Matches("tui.input.tab", tabKey))
            {
                WithEditorState(() => _suppressAutoCompletion = false);
                try
                {
                    await CompleteInputAsync(tabKey, cancellationToken);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    WithEditorState(() => { _completionCycle = null; });
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
                action = WithEditorState(() =>
                {
                    var result = _buffer.IsJumpModeActive && !next.IsPaste
                        ? _buffer.HandleJumpText(text)
                        : next.IsPaste ? _buffer.InsertPaste(text) : _buffer.InsertText(text);
                    if (next.IsPaste) _suppressAutoCompletion = true;
                    return result;
                });
                if (!next.IsExtensionRewrite && following?.Key is { } bufferedSubmitKey &&
                    _keymap.Matches("tui.input.submit", bufferedSubmitKey))
                {
                    next = following;
                    action = EditorAction.Submit;
                }
                else
                {
                    if (!next.IsExtensionRewrite && following is not null) input.PushBack(following);
                    if (action == EditorAction.Render)
                    {
                        if (!next.IsPaste) RefreshAutocomplete(completionForce);
                        Render();
                    }
                    continue;
                }
            }
            else
            {
                action = WithEditorState(() => _buffer.Handle(next.Key!.Value));
                var refreshesCompletion = next.Key is { } handledKey &&
                    (IsCompletionRefreshKey(handledKey) ||
                     hadAutocomplete && IsCursorMovementKey(handledKey));
                if (refreshesCompletion) WithEditorState(() => _suppressAutoCompletion = false);
                if (action == EditorAction.Render && refreshesCompletion)
                    RefreshAutocomplete(completionForce);
            }
            switch (action)
            {
                case EditorAction.Exit:
                    ExitInputLoop();
                    return null;
                case EditorAction.Cancel:
                    Render();
                    break;
                case EditorAction.Submit:
                    var submitted = WithEditorState(() =>
                    {
                        var value = _buffer.ExpandedText;
                        _buffer.Clear();
                        return value;
                    });
                    ClearLine();
                    if (_screen is not { IsActive: true } && !submitted.StartsWith("/", StringComparison.Ordinal))
                        Console.WriteLine($"{Prompt}{new string(submitted.Replace('\n', '↵').Select(c => char.IsControl(c) ? ' ' : c).ToArray())}");
                    return submitted;
                case EditorAction.Render: Render(); break;
            }
        }
    }

    internal bool RegisterClearPress()
    {
        var now = _clockMilliseconds();
        if (_lastClearAtMilliseconds is { } previous && now - previous < DoublePressWindowMilliseconds)
            return true;
        _lastClearAtMilliseconds = now;
        return false;
    }

    internal async Task<bool> HandleIdleEscapeAsync(Func<string, Task> dispatchApplicationAction)
    {
        if (WithEditorState(() => _completionCycle is not null))
        {
            WithEditorState(() =>
            {
                _completionCycle = null;
                _suppressAutoCompletion = true;
            });
            CancelCompletionRequest();
            return true;
        }
        CancelCompletionRequest();
        WithEditorState(() => _suppressAutoCompletion = true);
        var text = WithEditorState(() => _buffer.Text);
        if (text.TrimStart().StartsWith('!'))
        {
            WithEditorState(_buffer.Clear);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(text)) return true;
        var action = _getDoubleEscapeAction?.Invoke() ?? UserSettings.DefaultDoubleEscapeAction;
        // Pinned Pi only special-cases the exact strings "none" and "tree"; any other stored value
        // takes the fork path.
        if (action == "none") return true;
        var now = _clockMilliseconds();
        if (_lastEscapeAtMilliseconds is { } previous && now - previous < TerminalEditor.DoublePressWindowMilliseconds)
        {
            _lastEscapeAtMilliseconds = null;
            await DispatchApplicationActionAsync(dispatchApplicationAction,
                action == "tree" ? "app.session.tree" : "app.session.fork");
        }
        else _lastEscapeAtMilliseconds = now;
        return true;
    }

    /// <summary>Route input to the editor while an interactive Bash command runs; Escape aborts it.</summary>
    public async Task MonitorBashAsync(Action abort, Task operation, CancellationToken cancellationToken,
        Action? requestShutdown = null)
    {
        ArgumentNullException.ThrowIfNull(abort);
        ArgumentNullException.ThrowIfNull(operation);
        using var inputLoop = EnterInputLoop();
        try
        {
            using var mode = TerminalMode.Enter(_screen);
            var input = EnsureInput();
            Render();
            while (!operation.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                UpdateEditorGeometry();
                if (!TryReadExtensionInput(input, 30, out var next))
                {
                    _screen?.RefreshIfResized();
                    continue;
                }
                if (next.IsEndOfStream || next.IsControl) continue;
                if (InterceptExtensionInput(ref next)) continue;
                if (next.Mouse is { } mouse)
                {
                    var result = _screen?.HandleMouse(mouse) ?? default;
                    if (result.ClearEditorSelection) WithEditorState(_buffer.ClearSelection);
                    if (result.EditorCursorOffset is { } cursor) WithEditorState(() => _buffer.SetCursor(cursor));
                    Render();
                    continue;
                }
                if (next.Key is { } key && _keymap.Matches("app.interrupt", key))
                {
                    abort();
                    return;
                }
                if (next.Key is { } clearKey && _keymap.Matches("app.clear", clearKey))
                {
                    if (RegisterClearPress())
                    {
                        requestShutdown?.Invoke();
                        return;
                    }
                    WithEditorState(_buffer.Clear);
                    Render();
                    continue;
                }
                if (next.Text is not null) WithEditorState(() => _buffer.InsertText(next.Text));
                else if (next.Key is { } editorKey)
                {
                    var action = WithEditorState(() => _buffer.Handle(editorKey));
                    if (action == EditorAction.Submit)
                    {
                        var submitted = WithEditorState(() =>
                        {
                            if (!_buffer.TrySubmit(out var value)) return null;
                            _buffer.Clear();
                            return value;
                        });
                        if (!string.IsNullOrWhiteSpace(submitted))
                            _pendingBashSubmission = _pendingBashSubmission is null
                                ? submitted
                                : _pendingBashSubmission + "\n" + submitted;
                        continue;
                    }
                    if (action == EditorAction.Exit)
                    {
                        requestShutdown?.Invoke();
                        return;
                    }
                }
                Render();
            }
        }
        finally { ClearLine(); }
    }

    internal const int DoublePressWindowMilliseconds = 500;

    internal bool HandleIdleTranscriptInput(TerminalInputEvent next)
    {
        if (_transcriptSearch.IsActive)
        {
            _transcriptSearch.HandleInput(next);
            Render();
            return true;
        }
        if (next.Key is not { } key || _screen is not { IsActive: true } screen) return false;
        if (_keymap.Matches("tui.altScreen.search", key))
            _transcriptSearch.Start();
        else if (_keymap.Matches("tui.altScreen.pageUp", key))
            screen.ScrollPage(up: true);
        else if (_keymap.Matches("tui.altScreen.pageDown", key))
            screen.ScrollPage(up: false);
        else if (_keymap.Matches("tui.altScreen.previousPrompt", key))
            screen.ScrollToPreviousPrompt();
        else if (_keymap.Matches("tui.altScreen.nextPrompt", key))
            screen.ScrollToNextPrompt();
        else if (_keymap.Matches("tui.altScreen.top", key))
            screen.ScrollToTop();
        else if (_keymap.Matches("tui.altScreen.bottom", key))
            screen.ScrollToBottom();
        else
            return false;
        Render();
        return true;
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
        if (_keymap.Matches("tui.editor.historyPrevious", key) || _keymap.Matches("tui.editor.historyNext", key))
            return false;
        var action = _keymap.MatchIdleApplicationAction(key);
        if (action is null) return false;
        if (action != "app.suspend") beforeDispatch?.Invoke();
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
        var appliedExisting = WithEditorState(() =>
        {
            var snapshot = _buffer.CaptureSnapshot();
            if (_completionCycle is not { } previous || !previous.Matches(snapshot)) return false;
            ApplyCompletionCycleUnsafe(previous);
            return true;
        });
        if (appliedExisting) return;

        CancelCompletionRequest();
        var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requestToken = request.Token;
        var (snapshot, extensionProvider, force) = WithEditorState(() =>
        {
            _completionRequest = request;
            var current = _buffer.CaptureSnapshot();
            var before = TextBeforeCursor(current.Text, current.Cursor);
            return (current, _extensionAutocompleteProvider, !IsSlashCommandContext(before));
        });
        var nativeTask = _autocomplete.GetSuggestionsAsync(snapshot.Text, snapshot.Cursor, force, requestToken);
        if (!nativeTask.IsCompleted)
        {
            _ = FinishCompletionAsync(nativeTask, snapshot, request, requestToken, extensionProvider, force);
            return;
        }
        await FinishCompletionAsync(nativeTask, snapshot, request, requestToken, extensionProvider, force)
            .ConfigureAwait(false);
    }

    private async Task FinishCompletionAsync(Task<EditorCompletionResult?> nativeTask,
        EditorBufferSnapshot snapshot, CancellationTokenSource request, CancellationToken requestToken,
        Func<string, int, CancellationToken,
            Task<IReadOnlyList<NativeExtensions.PiSharpExtensionAutocompleteSuggestion>>>? extensionProvider,
        bool force)
    {
        try
        {
            var native = await nativeTask.ConfigureAwait(false);
            IReadOnlyList<NativeExtensions.PiSharpExtensionAutocompleteSuggestion> extensions = [];
            if (extensionProvider is not null)
                extensions = await extensionProvider(snapshot.Text, snapshot.Cursor, requestToken)
                    .ConfigureAwait(false);
            requestToken.ThrowIfCancellationRequested();

            WithEditorState(() =>
            {
                var current = _buffer.CaptureSnapshot();
                if (requestToken.IsCancellationRequested || current.Version != snapshot.Version)
                {
                    _completionCycle = null;
                    return;
                }

                var candidates = NativeCandidates(native);
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

                if (candidates.Count == 0)
                {
                    _completionCycle = null;
                    return;
                }

                if (force && candidates.Count == 1)
                {
                    ApplyCompletionCycleUnsafe(new(candidates, 0, force, current.Text, current.Cursor));
                    return;
                }

                _completionCycle = new(candidates, SelectBestMatch(candidates, native?.Prefix), force,
                    current.Text, current.Cursor);
            });
            Render();
        }
        catch (OperationCanceledException)
        {
            // Superseded or cancelled: a newer completion state owns the editor.
        }
        finally
        {
            Interlocked.CompareExchange(ref _completionRequest, null, request);
            request.Dispose();
        }
    }

    private static List<CompletionCandidate> NativeCandidates(EditorCompletionResult? result)
    {
        if (result is null) return [];
        return result.Items.Select(item => new CompletionCandidate(item.Label, item.Value, Native: true,
            item.Description, item.IsDirectory, item, result.Kind, result.Prefix)).ToList();
    }

    private void ApplyCompletionCycleUnsafe(CompletionCycle cycle)
    {
        var selected = cycle.Candidates[cycle.SelectedIndex];
        if (selected.Native && selected.Item is { } item)
        {
            _autocomplete.Apply(_buffer, new EditorCompletionResult(selected.Kind, selected.Prefix, [item]), item);
        }
        else
        {
            var snapshot = _buffer.CaptureSnapshot();
            var start = snapshot.Cursor;
            while (start > 0 && !char.IsWhiteSpace(snapshot.Text[start - 1])) start--;
            _buffer.Replace(start, snapshot.Cursor - start, selected.ReplacementText);
        }
        _completionCycle = null;
    }

    private void MoveCompletionSelection(int direction)
    {
        WithEditorState(() =>
        {
            if (_completionCycle is not { } cycle) return;
            var index = (cycle.SelectedIndex + direction + cycle.Candidates.Count) % cycle.Candidates.Count;
            _completionCycle = cycle with { SelectedIndex = index };
        });
    }

    private void RefreshAutocomplete(bool force = false)
    {
        CancelCompletionRequest();
        if (_suppressAutoCompletion)
        {
            WithEditorState(() => { _completionCycle = null; });
            return;
        }
        var request = new CancellationTokenSource();
        _completionRequest = request;
        var snapshot = WithEditorState(_buffer.CaptureSnapshot);
        _ = RefreshAutocompleteAsync(snapshot, request, request.Token, force);
    }

    private async Task RefreshAutocompleteAsync(EditorBufferSnapshot snapshot, CancellationTokenSource request,
        CancellationToken cancellationToken, bool force)
    {
        try
        {
            var result = await _autocomplete.GetSuggestionsAsync(snapshot.Text, snapshot.Cursor, force,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            WithEditorState(() =>
            {
                if (_suppressAutoCompletion) return;
                var current = _buffer.CaptureSnapshot();
                if (current.Version != snapshot.Version || current.Cursor != snapshot.Cursor) return;
                if (result is null || !force && result.Kind == EditorCompletionKind.Path &&
                    !result.Prefix.Contains('@'))
                {
                    _completionCycle = null;
                    return;
                }
                var candidates = NativeCandidates(result);
                _completionCycle = candidates.Count == 0 ? null : new(candidates,
                    SelectBestMatch(candidates, result.Prefix), force, snapshot.Text, snapshot.Cursor);
            });
            Render();
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.CompareExchange(ref _completionRequest, null, request);
            request.Dispose();
        }
    }

    /// <summary>Pi highlights an exact completion value, else the first value with the query as a prefix.</summary>
    private static int SelectBestMatch(IReadOnlyList<CompletionCandidate> candidates, string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return 0;
        var firstPrefix = -1;
        for (var index = 0; index < candidates.Count; index++)
        {
            var value = candidates[index].ReplacementText;
            if (string.Equals(value, prefix, StringComparison.Ordinal)) return index;
            if (firstPrefix < 0 && value.StartsWith(prefix, StringComparison.Ordinal)) firstPrefix = index;
        }
        return firstPrefix < 0 ? 0 : firstPrefix;
    }

    private static string TextBeforeCursor(string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, cursor - 1)) + 1;
        return text[lineStart..cursor];
    }

    private static bool IsSlashCommandContext(string before)
    {
        var trimmed = before.TrimStart();
        return trimmed.StartsWith('/') && !trimmed.Contains(' ');
    }

    /// <summary>Deletions can open or narrow a picker again, matching Pi's handleBackspace/handleForwardDelete.</summary>
    private bool IsCompletionRefreshKey(ConsoleKeyInfo key) =>
        _keymap.Matches("tui.editor.deleteCharBackward", key) ||
        _keymap.Matches("tui.editor.deleteCharForward", key) ||
        _keymap.Matches("tui.editor.deleteWordBackward", key) ||
        _keymap.Matches("tui.editor.deleteWordForward", key);

    private bool IsCursorMovementKey(ConsoleKeyInfo key) =>
        _keymap.Matches("tui.editor.cursorUp", key) ||
        _keymap.Matches("tui.editor.cursorDown", key) ||
        _keymap.Matches("tui.editor.cursorLeft", key) ||
        _keymap.Matches("tui.editor.cursorRight", key) ||
        _keymap.Matches("tui.editor.cursorWordLeft", key) ||
        _keymap.Matches("tui.editor.cursorWordRight", key) ||
        _keymap.Matches("tui.editor.cursorLineStart", key) ||
        _keymap.Matches("tui.editor.cursorLineEnd", key);

    private void CancelCompletionRequest()
    {
        CancelCompletionRequest(Interlocked.Exchange(ref _completionRequest, null));
    }

    private void CancelCompletionRequest(CancellationTokenSource? request)
    {
        if (request is null) return;
        try { request.Cancel(); }
        catch (Exception error)
        {
            try { Console.Error.WriteLine($"Extension autocomplete cancellation failed: {TerminalSafeText.Normalize(error.Message)}"); }
            catch (Exception) { }
        }
        finally { request.Dispose(); }
    }

    private T WithEditorState<T>(Func<T> action)
    {
        lock (_buffer.SyncRoot) return action();
    }

    private void WithEditorState(Action action)
    {
        lock (_buffer.SyncRoot) action();
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

    private void ExitInputLoop()
    {
        ClearLine();
        if (_screen is not { IsActive: true }) Console.WriteLine();
    }

    private sealed class InputLoopLease(Action release) : IDisposable
    {
        private int _active = 1;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) != 0) release();
        }
    }

    private void UpdateEditorGeometry()
    {
        var rows = TerminalScreen.ReadRows();
        var columns = TerminalScreen.ReadColumns();
        var width = _screen is { IsActive: true } ? _screen.EditorLayoutWidth : Math.Max(1, columns - 3);
        WithEditorState(() =>
        {
            _buffer.SetLayoutWidth(width);
            _buffer.SetTerminalRows(rows);
        });
    }

    private void Render()
    {
        UpdateEditorGeometry();
        if (_screen is { IsActive: true })
        {
            var (snapshot, suggestions) = WithEditorState(() =>
            {
                var searchingTranscript = _transcriptSearch.IsActive;
                var current = searchingTranscript
                    ? _transcriptSearch.CaptureEditorSnapshotForDisplay()
                    : _buffer.CaptureSnapshot();
                IReadOnlyList<EditorCompletionSuggestion> currentSuggestions = [];
                if (!searchingTranscript && _completionCycle is { } completionCycle &&
                    completionCycle.Matches(current))
                {
                    var maximum = Math.Max(1, _screen?.AutocompleteMaxVisible ?? 5);
                    var start = Math.Max(0, Math.Min(completionCycle.SelectedIndex - maximum + 1,
                        completionCycle.Candidates.Count - maximum));
                    var rendered = completionCycle.Candidates.Skip(start).Take(maximum)
                        .Select((candidate, index) => new EditorCompletionSuggestion(candidate.DisplayText,
                            candidate.Description, start + index == completionCycle.SelectedIndex)).ToList();
                    if (completionCycle.Candidates.Count > maximum)
                        rendered.Add(new EditorCompletionSuggestion(
                            $"({completionCycle.SelectedIndex + 1}/{completionCycle.Candidates.Count})", null,
                            Position: true));
                    currentSuggestions = rendered;
                }
                return (current, currentSuggestions);
            });
            if (suggestions.Count > 0 && snapshot.Text.StartsWith("/", StringComparison.Ordinal) &&
                !(_lastRenderedEditorText?.StartsWith("/", StringComparison.Ordinal) ?? false))
                _screen.SetEditor(snapshot.Text, snapshot.Cursor, snapshot.SelectionStart, snapshot.SelectionEnd,
                    [], immediate: true);
            _screen.SetEditor(snapshot.Text, snapshot.Cursor, snapshot.SelectionStart, snapshot.SelectionEnd,
                suggestions);
            _lastRenderedEditorText = snapshot.Text;
            return;
        }
        var width = TerminalScreen.ReadColumns();
        var height = TerminalScreen.ReadRows();
        var editor = WithEditorState(_buffer.CaptureSnapshot);
        var frame = EditorViewport.Layout(editor.Text, editor.Cursor, width, Math.Max(1, height / 3),
            scrollOffset: _normalEditorScrollOffset);
        _normalEditorScrollOffset = frame.ScrollOffset;
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
