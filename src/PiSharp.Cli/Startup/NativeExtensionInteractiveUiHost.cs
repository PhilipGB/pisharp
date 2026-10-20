using PiSharp.Runtime.Extensions;
using PiSharp.Cli.Protocols;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

/// <summary>Provides Pi-shaped extension dialogs before the main interactive screen is created.</summary>
internal sealed class NativeExtensionInteractiveUiHost(bool hasUserInterface, string mode, TextWriter output, TextWriter error)
    : IProjectTrustExtensionUi, IPiSharpExtensionUiPackageFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Owner, string Key), (long Order, string Text)> _statuses = [];
    private readonly Dictionary<(Guid Owner, string Key), WidgetEntry> _widgets = [];
    private readonly Dictionary<Guid, (long Order, ComponentEntry Component)> _headers = [];
    private readonly Dictionary<Guid, (long Order, ComponentEntry Component)> _footers = [];
    private readonly Dictionary<Guid, (long Order, ComponentEntry Component)> _editors = [];
    private readonly Dictionary<Guid, (long Order, string Title)> _titles = [];
    private readonly Dictionary<Guid, PresentationContribution> _presentationContributions = [];
    private readonly NativeExtensionTerminalInputRouter _inputRouter = new(error);
    private readonly List<AutocompleteEntry> _autocompleteProviders = [];
    private readonly List<(Guid Owner, string Message, string Type)> _pendingRpcNotifications = [];
    private readonly List<(Guid Owner, string Text)> _pendingRpcEditorUpdates = [];
    private RpcExtensionUiTransport? _rpcUi;
    private bool _rpcUiHasStarted;
    private NativeExtensionCustomComponentRequest? _activeCustom;
    private Action<string, string>? _interactiveNotificationHandler;
    private TerminalScreen? _interactiveScreen;
    private TerminalEditor? _interactiveEditor;
    private TerminalPresentation? _terminalPresentation;
    private long _interactiveBindingId;
    private long _nextOwnerOrder;
    private long _nextAutocompleteOrder;
    private long _nextContributionOrder;
    private long _presentationVersion;
    private int _presentationRefreshQueued;
    private Timer? _presentationTimer;
    private Timer? _workingTimer;
    private int _workingFrameIndex;
    private bool CanShowDialog => hasUserInterface && mode == "interactive";

    internal void BindRpcUiTransport(RpcExtensionUiTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (mode != "rpc") throw new InvalidOperationException("RPC UI transport can only bind in RPC mode.");
        lock (_gate)
        {
            if (_rpcUi is not null && !ReferenceEquals(_rpcUi, transport))
                throw new InvalidOperationException("An RPC UI transport is already bound.");
            _rpcUi = transport;
            _rpcUiHasStarted = true;
            foreach (var status in _statuses.Select(pair => (pair.Key.Key, pair.Value.Text, pair.Value.Order))
                .GroupBy(item => item.Key, StringComparer.Ordinal)
                .Select(group => group.MaxBy(item => item.Order))
                .OrderBy(item => item.Order).ThenBy(item => item.Key, StringComparer.Ordinal))
                transport.SetStatus(status.Key, status.Text);
            foreach (var widget in _widgets.Where(pair => pair.Value.Component is null)
                .GroupBy(pair => pair.Key.Key, StringComparer.Ordinal)
                .Select(group => group.MaxBy(item => item.Value.Order))
                .OrderBy(item => item.Value.Order).ThenBy(item => item.Key.Key, StringComparer.Ordinal))
                transport.SetWidget(widget.Key.Key,
                    widget.Value.Lines.Select(line => line.Text).ToArray(), widget.Value.Placement);
            var title = _titles.Count == 0 ? null : _titles.MaxBy(pair => pair.Value.Order).Value.Title;
            if (title is not null) transport.SetTitle(title);
            foreach (var editorUpdate in _pendingRpcEditorUpdates)
                transport.SetEditorText(editorUpdate.Text);
            _pendingRpcEditorUpdates.Clear();
            foreach (var notification in _pendingRpcNotifications)
                transport.Notify(notification.Message, notification.Type);
            _pendingRpcNotifications.Clear();
        }
    }

    internal void UnbindRpcUiTransport(RpcExtensionUiTransport transport)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_rpcUi, transport)) _rpcUi = null;
        }
    }

    internal void SetInteractiveNotificationHandler(Action<string, string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _interactiveNotificationHandler = handler;
    }

    internal IDisposable BindInteractiveScreen(TerminalScreen screen, TerminalEditor? editor = null,
        TerminalPresentation? presentation = null)
    {
        ArgumentNullException.ThrowIfNull(screen);
        long bindingId;
        lock (_gate)
        {
            _interactiveScreen = screen;
            _interactiveEditor = editor;
            _terminalPresentation = presentation;
            bindingId = ++_interactiveBindingId;
        }
        screen.SetExtensionUiContextChangedHandler(HandleScreenContextChanged);
        editor?.SetExtensionInputInterceptor(InterceptTerminalInput);
        editor?.SetExtensionEditorInputHandler(HandleEditorInput);
        editor?.SetExtensionAutocompleteProvider(GetAutocompleteSuggestionsAsync);
        UpdateTerminalTitle();
        UpdateWorkingPresentation();
        UpdateHiddenThinkingLabel();
        RefreshPresentation();
        return new ScreenBinding(this, screen, editor, bindingId);
    }

    public IPiSharpExtensionUiPackageScope CreatePackageScope(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return new NativeExtensionUiPackageScope(this, sourcePath, Guid.NewGuid(),
            Interlocked.Increment(ref _nextOwnerOrder));
    }

    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken = default) =>
        SelectAsync(Guid.Empty, title, options, cancellationToken, CancellationToken.None);

    internal async Task<string?> SelectAsync(Guid ownerId, string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken, CancellationToken packageLifetime)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == "rpc")
        {
            var rpcUi = Volatile.Read(ref _rpcUi);
            if (rpcUi is null) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
            return await rpcUi.SelectAsync(title, options, cancellationToken: linked.Token).ConfigureAwait(false);
        }
        if (!CanShowDialog || options.Count == 0) return null;

        if (HasActiveInteractiveScreen())
        {
            var liveOptions = options.Select((option, index) =>
                new TerminalSelectionOption<string>(index.ToString(), option, option)).ToArray();
            var result = await ShowCustomAsync<string>(ownerId,
                complete => new NativeExtensionSelectionComponent<string>(title, liveOptions, complete),
                new(NativeExtensions.PiSharpExtensionUiHostingMode.Overlay), cancellationToken,
                packageLifetime).ConfigureAwait(false);
            return result.IsCancelled ? null : result.Value;
        }

        using var screen = CreateScreen(out var editor);
        var terminalOptions = options.Select((option, index) =>
            new TerminalSelectionOption<string>(index.ToString(), option, option)).ToArray();
        var selected = editor.ShowInlineSelectionList(title, terminalOptions,
            footer: "↑↓ navigate  enter select  escape/ctrl+c cancel",
            cancellationToken: cancellationToken, extensionDialog: true);
        return selected?.Option.Value;
    }

    internal async Task<string?> SelectWithOptionsAsync(Guid ownerId, string title, IReadOnlyList<string> options,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions, CancellationToken cancellationToken,
        CancellationToken packageLifetime)
    {
        if (mode == "rpc")
        {
            var rpcUi = Volatile.Read(ref _rpcUi);
            if (rpcUi is null) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
            return await rpcUi.SelectAsync(title, options, dialogOptions, linked.Token).ConfigureAwait(false);
        }
        return await WithDialogTimeoutAsync(dialogOptions?.Timeout, cancellationToken, null,
            token => SelectAsync(ownerId, title, options, token, packageLifetime)).ConfigureAwait(false);
    }

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
        ConfirmAsync(Guid.Empty, title, message, cancellationToken, CancellationToken.None);

    internal async Task<bool> ConfirmAsync(Guid ownerId, string title, string message,
        CancellationToken cancellationToken, CancellationToken packageLifetime)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == "rpc")
        {
            var rpcUi = Volatile.Read(ref _rpcUi);
            if (rpcUi is null) return false;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
            return await rpcUi.ConfirmAsync(title, message, cancellationToken: linked.Token).ConfigureAwait(false);
        }
        if (!CanShowDialog) return false;

        if (HasActiveInteractiveScreen())
        {
            var liveOptions = new[]
            {
                new TerminalSelectionOption<bool>("yes", true, "Yes"),
                new TerminalSelectionOption<bool>("no", false, "No")
            };
            var result = await ShowCustomAsync<bool>(ownerId,
                complete => new NativeExtensionSelectionComponent<bool>(title + "\n" + message, liveOptions, complete),
                new(NativeExtensions.PiSharpExtensionUiHostingMode.Overlay), cancellationToken,
                packageLifetime).ConfigureAwait(false);
            return !result.IsCancelled && result.Value;
        }

        using var screen = CreateScreen(out var editor);
        var options = new[]
        {
            new TerminalSelectionOption<bool>("yes", true, "Yes"),
            new TerminalSelectionOption<bool>("no", false, "No")
        };
        var selected = editor.ShowInlineSelectionList($"{title}\n{message}", options,
            footer: "↑↓ navigate  enter select  escape/ctrl+c cancel",
            cancellationToken: cancellationToken, extensionDialog: true);
        return selected?.Option.Value ?? false;
    }

    internal async Task<bool> ConfirmWithOptionsAsync(Guid ownerId, string title, string message,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions, CancellationToken cancellationToken,
        CancellationToken packageLifetime)
    {
        if (mode == "rpc")
        {
            var rpcUi = Volatile.Read(ref _rpcUi);
            if (rpcUi is null) return false;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
            return await rpcUi.ConfirmAsync(title, message, dialogOptions, linked.Token).ConfigureAwait(false);
        }
        return await WithDialogTimeoutAsync(dialogOptions?.Timeout, cancellationToken, false,
            token => ConfirmAsync(ownerId, title, message, token, packageLifetime)).ConfigureAwait(false);
    }

    public Task<string?> InputAsync(string title, string? placeholder = null,
        CancellationToken cancellationToken = default) =>
        InputAsync(Guid.Empty, title, placeholder, cancellationToken, CancellationToken.None);

    internal async Task<string?> InputAsync(Guid ownerId, string title, string? placeholder,
        CancellationToken cancellationToken, CancellationToken packageLifetime)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == "rpc")
        {
            var rpcUi = Volatile.Read(ref _rpcUi);
            if (rpcUi is null) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
            return await rpcUi.InputAsync(title, placeholder, cancellationToken: linked.Token).ConfigureAwait(false);
        }
        if (!CanShowDialog) return null;

        if (HasActiveInteractiveScreen() && GetInteractiveEditor() is { } liveEditor)
        {
            var buffer = liveEditor.CreateExtensionDialogBuffer();
            var result = await ShowCustomAsync<string>(ownerId,
                complete => new NativeExtensionInputDialogComponent(buffer, title, placeholder, complete),
                new(NativeExtensions.PiSharpExtensionUiHostingMode.Overlay), cancellationToken,
                packageLifetime).ConfigureAwait(false);
            return result.IsCancelled ? null : result.Value;
        }

        using var screen = CreateScreen(out var editor);
        var values = await editor.PromptSequenceAsync(title, [(title, placeholder)],
            cancellationToken: cancellationToken, extensionDialog: true).ConfigureAwait(false);
        return values?.FirstOrDefault();
    }

    internal async Task<string?> InputWithOptionsAsync(Guid ownerId, string title, string? placeholder,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions, CancellationToken cancellationToken,
        CancellationToken packageLifetime)
    {
        if (mode == "rpc")
        {
            var rpcUi = Volatile.Read(ref _rpcUi);
            if (rpcUi is null) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
            return await rpcUi.InputAsync(title, placeholder, dialogOptions, linked.Token).ConfigureAwait(false);
        }
        return await WithDialogTimeoutAsync(dialogOptions?.Timeout, cancellationToken, null,
            token => InputAsync(ownerId, title, placeholder, token, packageLifetime)).ConfigureAwait(false);
    }

    private static async Task<T> WithDialogTimeoutAsync<T>(TimeSpan? timeout, CancellationToken cancellationToken,
        T fallback, Func<CancellationToken, Task<T>> showDialog)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout is not { } duration) return await showDialog(cancellationToken).ConfigureAwait(false);
        if (duration < TimeSpan.Zero && duration != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Dialog timeout cannot be negative.");
        if (duration == TimeSpan.Zero) return fallback;
        if (duration == Timeout.InfiniteTimeSpan) return await showDialog(cancellationToken).ConfigureAwait(false);
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timed.CancelAfter(duration > TimeSpan.FromMilliseconds(uint.MaxValue - 1)
            ? TimeSpan.FromMilliseconds(uint.MaxValue - 1) : duration);
        try { return await showDialog(timed.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return fallback; }
    }

    private bool HasActiveInteractiveScreen()
    {
        lock (_gate) return _interactiveScreen is { IsActive: true };
    }

    private TerminalEditor? GetInteractiveEditor()
    {
        lock (_gate) return _interactiveEditor;
    }

    public void Notify(string message, string type = "info")
        => Notify(Guid.Empty, message, type);

    internal void Notify(Guid owner, string message, string type = "info")
    {
        ArgumentNullException.ThrowIfNull(message);
        if (mode == "rpc")
        {
            var handled = false;
            lock (_gate)
            {
                if (_rpcUi is RpcExtensionUiTransport rpcUi)
                {
                    rpcUi.Notify(message, type);
                    handled = true;
                }
                else if (!_rpcUiHasStarted)
                {
                    _pendingRpcNotifications.Add((owner, message, type));
                    handled = true;
                }
            }
            if (handled) return;
        }
        if (mode == "interactive")
        {
            lock (_gate) _interactiveNotificationHandler?.Invoke(message, type);
            return;
        }
        var safeMessage = TerminalSafeText.Normalize(message);
        var color = type switch
        {
            "error" => "31",
            "warning" => "33",
            _ => "36"
        };
        var styledMessage = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null
            ? $"\u001b[{color}m{safeMessage}\u001b[0m"
            : safeMessage;
        error.WriteLine(styledMessage);
        error.Flush();
    }

    internal void SetStatus(Guid owner, long order, string key, string? text)
    {
        key = NormalizeExtensionKey(key, nameof(key));
        if (text is not null)
        {
            text = NormalizeOneLine(text);
            if (text.Length > 4096) text = text[..4096];
        }
        lock (_gate)
        {
            if (text is null) _statuses.Remove((owner, key));
            else _statuses[(owner, key)] = (order, text);
            EmitRpcStatusUnsafe(key);
        }
        RefreshPresentation();
    }

    private void EmitRpcStatusUnsafe(string key)
    {
        if (_rpcUi is null || mode != "rpc") return;
        var text = _statuses.Where(pair => pair.Key.Key == key).OrderBy(pair => pair.Value.Order)
            .Select(pair => (string?)pair.Value.Text).LastOrDefault();
        _rpcUi.SetStatus(key, text);
    }

    internal void SetWorkingMessage(Guid owner, string? message)
    {
        var contributionOrder = Interlocked.Increment(ref _nextContributionOrder);
        lock (_gate)
        {
            var current = _presentationContributions.GetValueOrDefault(owner) ?? new();
            _presentationContributions[owner] = current with
            {
                WorkingMessageOrder = contributionOrder,
                WorkingMessage = string.IsNullOrWhiteSpace(message) ? null : NormalizeOneLine(message)
            };
        }
        UpdateWorkingPresentation();
    }

    internal void SetWorkingVisible(Guid owner, bool visible)
    {
        var contributionOrder = Interlocked.Increment(ref _nextContributionOrder);
        lock (_gate)
        {
            var current = _presentationContributions.GetValueOrDefault(owner) ?? new();
            _presentationContributions[owner] = current with
            {
                WorkingVisibleOrder = contributionOrder,
                WorkingVisible = visible
            };
        }
        UpdateWorkingPresentation();
    }

    internal void SetWorkingIndicator(Guid owner,
        NativeExtensions.PiSharpExtensionWorkingIndicatorOptions? options)
    {
        var contributionOrder = Interlocked.Increment(ref _nextContributionOrder);
        if (options is not null)
        {
            if (options.Frames is null || options.Frames.Count > 32)
                throw new ArgumentException("Working indicator must define at most 32 frames.", nameof(options));
            var interval = options.Interval ?? TimeSpan.FromMilliseconds(80);
            if (interval < TimeSpan.FromMilliseconds(50) || interval > TimeSpan.FromSeconds(5))
                throw new ArgumentOutOfRangeException(nameof(options), "Working indicator interval must be between 50 ms and 5 s.");
            options = new(options.Frames.Select(frame =>
            {
                if (frame is null) throw new ArgumentException("Working indicator frames cannot be null.", nameof(options));
                if (frame.Length == 0) return string.Empty;
                if (string.IsNullOrWhiteSpace(frame)) throw new ArgumentException("Working indicator frames cannot be whitespace.", nameof(options));
                var safe = NormalizeOneLine(frame);
                return safe.Length > 16 ? safe[..16] : safe;
            }).ToArray(), interval);
        }
        lock (_gate)
        {
            var current = _presentationContributions.GetValueOrDefault(owner) ?? new();
            _presentationContributions[owner] = current with
            {
                WorkingIndicatorOrder = contributionOrder,
                WorkingIndicator = options
            };
            _workingFrameIndex = 0;
        }
        UpdateWorkingPresentation();
    }

    internal void SetHiddenThinkingLabel(Guid owner, string? label)
    {
        var contributionOrder = Interlocked.Increment(ref _nextContributionOrder);
        lock (_gate)
        {
            var current = _presentationContributions.GetValueOrDefault(owner) ?? new();
            _presentationContributions[owner] = current with
            {
                HiddenThinkingLabelOrder = contributionOrder,
                HiddenThinkingLabel = string.IsNullOrWhiteSpace(label) ? null : NormalizeOneLine(label)
            };
        }
        UpdateHiddenThinkingLabel();
    }

    private void UpdateWorkingPresentation()
    {
        TerminalScreen? screen;
        PresentationContribution[] contributions;
        lock (_gate)
        {
            screen = _interactiveScreen;
            contributions = _presentationContributions.Values.ToArray();
        }
        var message = contributions.Where(item => item.WorkingMessage is not null)
            .MaxBy(item => item.WorkingMessageOrder)?.WorkingMessage;
        var visibility = contributions.Where(item => item.WorkingVisible is not null)
            .MaxBy(item => item.WorkingVisibleOrder)?.WorkingVisible;
        var indicator = contributions.Where(item => item.WorkingIndicator is not null)
            .MaxBy(item => item.WorkingIndicatorOrder)?.WorkingIndicator;
        var frame = indicator is null ? "⠋" : indicator.Frames.Count == 0 ? string.Empty :
            indicator.Frames.ElementAtOrDefault(_workingFrameIndex) ?? string.Empty;
        lock (_gate)
        {
            if (screen is { HasActiveWorkingStatus: true } && visibility != false && indicator is { Frames.Count: > 1 })
            {
                _workingTimer ??= new Timer(static state => ((NativeExtensionInteractiveUiHost)state!).AdvanceWorkingFrame(),
                    this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _workingTimer.Change(indicator.Interval ?? TimeSpan.FromMilliseconds(80),
                    indicator.Interval ?? TimeSpan.FromMilliseconds(80));
            }
            else
            {
                _workingTimer?.Dispose();
                _workingTimer = null;
            }
        }
        if (screen is not null)
        {
            lock (_gate)
                if (ReferenceEquals(_interactiveScreen, screen))
                    screen.SetExtensionWorkingPresentation(message, visibility, frame);
        }
    }

    private void AdvanceWorkingFrame()
    {
        TerminalScreen? screen;
        string frame;
        lock (_gate)
        {
            var indicator = _presentationContributions.Values.Where(item => item.WorkingIndicator is not null)
                .MaxBy(item => item.WorkingIndicatorOrder)?.WorkingIndicator;
            if (indicator is not { Frames.Count: > 1 }) return;
            _workingFrameIndex = (_workingFrameIndex + 1) % indicator.Frames.Count;
            frame = indicator.Frames[_workingFrameIndex];
            screen = _interactiveScreen;
            if (screen is not null) screen.SetWorkingFrame(frame);
        }
    }

    private void HandleScreenContextChanged()
    {
        UpdateWorkingPresentation();
        RefreshPresentation();
    }

    private void UpdateHiddenThinkingLabel()
    {
        TerminalScreen? screen;
        string? label;
        lock (_gate)
        {
            screen = _interactiveScreen;
            label = _presentationContributions.Values.Where(item => item.HiddenThinkingLabel is not null)
                .MaxBy(item => item.HiddenThinkingLabelOrder)?.HiddenThinkingLabel;
        }
        if (screen is not null)
        {
            lock (_gate)
                if (ReferenceEquals(_interactiveScreen, screen)) screen.SetHiddenThinkingLabel(label);
        }
    }

    internal void SetTitle(Guid owner, string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        title = NormalizeOneLine(title);
        if (title.Length > 256) title = title[..256];
        lock (_gate)
        {
            if (title.Length == 0) _titles.Remove(owner);
            else _titles[owner] = (Interlocked.Increment(ref _nextContributionOrder), title);
        }
        UpdateTerminalTitle();
    }

    private void UpdateTerminalTitle()
    {
        TerminalScreen? screen;
        RpcExtensionUiTransport? rpcUi;
        string title;
        lock (_gate)
        {
            screen = _interactiveScreen;
            title = _titles.Count == 0 ? "" : _titles.MaxBy(pair => pair.Value.Order).Value.Title;
            rpcUi = mode == "rpc" ? _rpcUi : null;
            rpcUi?.SetTitle(title);
        }
        if (screen is not null)
        {
            lock (_gate)
                if (ReferenceEquals(_interactiveScreen, screen)) screen.SetExtensionTitle(title);
        }
    }

    internal NativeExtensions.PiSharpExtensionUiThemeResult SetTheme(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(false, "Theme name cannot be empty.");
        TerminalPresentation? presentation;
        lock (_gate) presentation = _terminalPresentation;
        if (presentation is null) return new(false, "Interactive theme selection is unavailable.");
        var result = presentation.SetExtensionTheme(name);
        if (result.Success)
            RefreshPresentation();
        return result;
    }

    internal NativeExtensions.PiSharpExtensionUiTheme? GetTheme(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        TerminalPresentation? presentation;
        TerminalScreen? screen;
        lock (_gate)
        {
            presentation = _terminalPresentation;
            screen = _interactiveScreen;
        }
        if (presentation is null || screen is null) return null;
        try
        {
            var theme = presentation.Themes.Load(name);
            return screen.CreateExtensionUiRenderContext(new Dictionary<string, string>()).Theme with
            {
                Name = theme.Name,
                Styles = TerminalScreen.CreateExtensionUiThemeStyles(theme)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    internal IReadOnlyList<(string Name, string? Path)> GetAllThemes()
    {
        TerminalPresentation? presentation;
        lock (_gate) presentation = _terminalPresentation;
        return presentation?.Themes.GetAllThemes() ?? [];
    }

    internal bool GetToolsExpanded()
    {
        lock (_gate) return _interactiveScreen?.ToolResultsExpanded ?? false;
    }

    internal void SetToolsExpanded(bool expanded)
    {
        lock (_gate) _interactiveScreen?.SetToolResultsExpanded(expanded);
    }

    internal void SetEditorText(string text)
        => SetEditorText(Guid.Empty, text);

    internal void SetEditorText(Guid owner, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (mode == "rpc")
        {
            lock (_gate)
            {
                if (_rpcUi is RpcExtensionUiTransport rpcUi) rpcUi.SetEditorText(text);
                else _pendingRpcEditorUpdates.Add((owner, text));
            }
            return;
        }
        lock (_gate) _interactiveEditor?.SetDraft(text);
    }

    internal void PasteToEditor(string text)
        => PasteToEditor(Guid.Empty, text);

    internal void PasteToEditor(Guid owner, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (mode == "rpc")
        {
            SetEditorText(owner, text);
            return;
        }
        lock (_gate) _interactiveEditor?.InsertTextAtCursor(text);
    }

    internal string GetEditorText()
    {
        lock (_gate) return _interactiveEditor?.Draft ?? string.Empty;
    }

    internal async Task<string?> EditAsync(Guid ownerId, string title, string? prefill,
        CancellationToken cancellationToken, CancellationToken packageLifetime)
    {
        ArgumentNullException.ThrowIfNull(title);
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == "rpc")
        {
            var rpcUi = Volatile.Read(ref _rpcUi);
            if (rpcUi is null) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
            return await rpcUi.EditAsync(title, prefill, linked.Token).ConfigureAwait(false);
        }
        TerminalEditor? editor;
        lock (_gate)
        {
            if (_activeCustom is not null) return null;
            editor = _interactiveEditor;
        }
        if (!CanShowDialog || editor is null || !editor.IsScreenActive)
            return null;

        var savedState = editor.BeginExtensionEdit(prefill);
        try
        {
            var result = await ShowCustomAsync<string>(ownerId,
                complete => new EditorEditComponent(editor, title, complete),
                new(NativeExtensions.PiSharpExtensionUiHostingMode.Focused),
                cancellationToken, packageLifetime).ConfigureAwait(false);
            return result.IsCancelled ? null : result.Value;
        }
        finally
        {
            editor.EndExtensionEdit(savedState);
        }
    }

    internal void SetEditorComponent(Guid owner,
        NativeExtensions.IPiSharpExtensionEditorComponent? component)
    {
        if (mode == "rpc")
        {
            DisposeUnsupportedComponent(component, "editor");
            return;
        }
        ComponentEntry? removed = null;
        ComponentEntry? added = null;
        lock (_gate)
        {
            if (_editors.Remove(owner, out var existing)) removed = existing.Component;
            if (component is not null)
            {
                added = removed is not null && ReferenceEquals(removed.Component, component)
                    ? removed
                    : new(component);
                _editors[owner] = (Interlocked.Increment(ref _nextContributionOrder), added);
            }
        }
        if (!ReferenceEquals(removed, added)) removed?.Dispose();
        if (!ReferenceEquals(removed, added)) added?.AttachRedraw(QueuePresentationRefresh);
        RefreshPresentation();
    }

    internal NativeExtensions.IPiSharpExtensionEditorComponent? GetEditorComponent()
    {
        lock (_gate)
            return _editors.Count == 0 ? null : _editors.MaxBy(pair => pair.Value.Order).Value.Component.EditorComponent;
    }

    internal IDisposable AddAutocompleteProvider(Guid owner, long order,
        NativeExtensions.PiSharpExtensionAutocompleteProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (mode == "rpc") return EmptyRegistration.Instance;
        var entry = new AutocompleteEntry(owner, order,
            Interlocked.Increment(ref _nextAutocompleteOrder), provider);
        lock (_gate) _autocompleteProviders.Add(entry);
        return new AutocompleteRegistration(this, entry);
    }

    private async Task<IReadOnlyList<NativeExtensions.PiSharpExtensionAutocompleteSuggestion>>
        GetAutocompleteSuggestionsAsync(string text, int cursor, CancellationToken cancellationToken)
    {
        AutocompleteEntry[] providers;
        lock (_gate) providers = _autocompleteProviders.OrderBy(provider => provider.Order)
            .ThenBy(provider => provider.RegistrationOrder).ToArray();
        var start = cursor;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var query = text[start..cursor];
        var results = new List<NativeExtensions.PiSharpExtensionAutocompleteSuggestion>();
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                provider.LifetimeToken);
            if (!provider.IsActive) continue;
            try
            {
                var supplied = await provider.Provider(new(text, cursor, query, linked.Token), linked.Token)
                    .ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                foreach (var suggestion in (supplied ?? []).Take(100))
                {
                    if (suggestion is null) continue;
                    var replacement = TerminalSafeText.Normalize(suggestion.ReplacementText ?? string.Empty);
                    var display = TerminalSafeText.Normalize(suggestion.DisplayText ?? string.Empty);
                    if (replacement.Length == 0 || display.Length == 0) continue;
                    var description = suggestion.Description is null ? null : TerminalSafeText.Normalize(
                        suggestion.Description[..Math.Min(suggestion.Description.Length, 1024)]);
                    results.Add(new(display[..Math.Min(display.Length, 512)],
                        replacement[..Math.Min(replacement.Length, 4096)], description));
                    if (results.Count == 100) return results;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!provider.IsActive) { }
            catch (Exception exception)
            {
                ReportAutocompleteFailure(provider.OwnerId, exception);
            }
        }
        return results;
    }

    private bool HandleEditorInput(TerminalInputEvent input)
    {
        ComponentEntry? component;
        TerminalScreen? screen;
        lock (_gate)
        {
            component = _editors.Count == 0 ? null : _editors.MaxBy(pair => pair.Value.Order).Value.Component;
            screen = _interactiveScreen;
        }
        if (component is null) return false;
        var result = component.HandleInput(ToExtensionInput(input),
            message => { if (screen is not null) ReportRenderFailure(screen, "editor", message); }, out _);
        return result == NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
    }

    internal static NativeExtensions.PiSharpExtensionUiInputEvent ToExtensionInput(TerminalInputEvent input)
    {
        var text = input.Text;
        if (text is null && TerminalInput.TryGetText(input, out var keyText)) text = keyText;
        var key = input.Key;
        return new(key?.Key.ToString(), text, input.IsPaste,
            key?.Modifiers.HasFlag(ConsoleModifiers.Shift) == true,
            key?.Modifiers.HasFlag(ConsoleModifiers.Alt) == true,
            key?.Modifiers.HasFlag(ConsoleModifiers.Control) == true)
        {
            RawData = input.RawData ?? text ?? key?.Key.ToString(),
            Mouse = input.Mouse is { } mouse
                ? new(mouse.Button, mouse.Column, mouse.Row, mouse.IsRelease)
                : null
        };
    }

    internal static TerminalInputEvent ToTerminalInput(NativeExtensions.PiSharpExtensionUiInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ConsoleKeyInfo? key = null;
        if (input.Key is { } keyName && Enum.TryParse<ConsoleKey>(keyName, ignoreCase: true, out var consoleKey))
        {
            var character = input.Text is { Length: 1 } text ? text[0] : '\0';
            key = new(character, consoleKey, input.Shift, input.Alt, input.Control);
        }
        return new(key, input.Text, input.Mouse is { } mouse
            ? new TerminalMouseEvent(mouse.Button, mouse.Column, mouse.Row, mouse.IsRelease)
            : null)
        {
            RawData = input.RawData,
            IsPaste = input.IsPaste
        };
    }

    private void ReportAutocompleteFailure(Guid owner, Exception exception) =>
        TryWriteError($"Extension autocomplete provider failed for {owner:N}: {exception.Message}");

    private void RemoveAutocompleteProvider(AutocompleteEntry entry)
    {
        lock (_gate) _autocompleteProviders.Remove(entry);
        DisposeAutocompleteProvider(entry);
    }

    private void DisposeAutocompleteProvider(AutocompleteEntry entry)
    {
        try { entry.Dispose(); }
        catch (Exception exception)
        {
            TryWriteError($"Extension autocomplete provider cancellation failed for {entry.OwnerId:N}: " +
                exception.Message);
        }
    }

    internal NativeExtensions.PiSharpExtensionUiTheme GetCurrentTheme()
    {
        TerminalScreen? screen;
        lock (_gate) screen = _interactiveScreen;
        return screen?.CreateExtensionUiRenderContext(new Dictionary<string, string>()).Theme ??
            NativeExtensions.PiSharpExtensionUiTheme.Empty;
    }

    internal void SetWidget(Guid owner, long order, string key, IReadOnlyList<string>? lines,
        NativeExtensions.PiSharpExtensionWidgetPlacement placement)
    {
        key = NormalizeExtensionKey(key, nameof(key));
        if (placement is not (NativeExtensions.PiSharpExtensionWidgetPlacement.AboveEditor or
            NativeExtensions.PiSharpExtensionWidgetPlacement.BelowEditor))
            throw new ArgumentOutOfRangeException(nameof(placement));
        WidgetEntry? removed = null;
        lock (_gate)
        {
            _widgets.Remove((owner, key), out removed);
            if (lines is not null)
            {
                var safeLines = lines.Take(100).Select(line => new NativeExtensions.PiSharpExtensionUiLine(
                    TerminalSafeText.Normalize((line ?? string.Empty)[..Math.Min((line ?? string.Empty).Length, 4096)]))).ToArray();
                _widgets[(owner, key)] = new(order, placement, safeLines, null);
            }
            EmitRpcWidgetUnsafe(key);
        }
        removed?.Dispose();
        RefreshPresentation();
    }

    internal void SetWidget(Guid owner, long order, string key,
        NativeExtensions.IPiSharpExtensionUiComponent? component,
        NativeExtensions.PiSharpExtensionWidgetPlacement placement)
    {
        key = NormalizeExtensionKey(key, nameof(key));
        if (mode == "rpc")
        {
            if (component is not null)
            {
                DisposeUnsupportedComponent(component, key);
                return;
            }
            SetWidget(owner, order, key, (IReadOnlyList<string>?)null, placement);
            return;
        }
        if (placement is not (NativeExtensions.PiSharpExtensionWidgetPlacement.AboveEditor or
            NativeExtensions.PiSharpExtensionWidgetPlacement.BelowEditor))
        {
            component?.Dispose();
            throw new ArgumentOutOfRangeException(nameof(placement));
        }
        WidgetEntry? removed = null;
        ComponentEntry? added = null;
        lock (_gate)
        {
            _widgets.Remove((owner, key), out removed);
            if (component is not null)
            {
                added = removed?.Component is { } previous && ReferenceEquals(previous.Component, component)
                    ? previous
                    : new(component);
                _widgets[(owner, key)] = new(order, placement, [], added);
            }
        }
        if (!ReferenceEquals(removed?.Component, added)) removed?.Dispose();
        if (!ReferenceEquals(removed?.Component, added)) added?.AttachRedraw(QueuePresentationRefresh);
        RefreshPresentation();
    }

    private void EmitRpcWidgetUnsafe(string key)
    {
        if (_rpcUi is null || mode != "rpc") return;
        var widget = _widgets.Where(pair => pair.Key.Key == key)
            .OrderByDescending(pair => pair.Value.Order).FirstOrDefault();
        if (widget.Value is null)
        {
            _rpcUi.SetWidget(key, null, NativeExtensions.PiSharpExtensionWidgetPlacement.AboveEditor);
            return;
        }
        if (widget.Value.Component is null)
            _rpcUi.SetWidget(key, widget.Value.Lines.Select(line => line.Text).ToArray(), widget.Value.Placement);
    }

    private void SetSlot(Dictionary<Guid, (long Order, ComponentEntry Component)> slots,
        Guid owner, long order, NativeExtensions.IPiSharpExtensionUiComponent? component)
    {
        ComponentEntry? removed = null;
        ComponentEntry? added = null;
        lock (_gate)
        {
            if (slots.Remove(owner, out var existing)) removed = existing.Component;
            if (component is not null)
            {
                added = removed is not null && ReferenceEquals(removed.Component, component)
                    ? removed
                    : new(component);
                slots[owner] = (order, added);
            }
        }
        if (!ReferenceEquals(removed, added)) removed?.Dispose();
        if (!ReferenceEquals(removed, added)) added?.AttachRedraw(QueuePresentationRefresh);
        RefreshPresentation();
    }

    internal void SetHeader(Guid owner, NativeExtensions.IPiSharpExtensionUiComponent? component)
    {
        if (mode == "rpc") { DisposeUnsupportedComponent(component, "header"); return; }
        SetSlot(_headers, owner, Interlocked.Increment(ref _nextContributionOrder), component);
    }

    internal void SetFooter(Guid owner, NativeExtensions.IPiSharpExtensionUiComponent? component)
    {
        if (mode == "rpc") { DisposeUnsupportedComponent(component, "footer"); return; }
        SetSlot(_footers, owner, Interlocked.Increment(ref _nextContributionOrder), component);
    }

    private void DisposeUnsupportedComponent(NativeExtensions.IPiSharpExtensionUiComponent? component, string name)
    {
        if (component is null) return;
        try { component.Dispose(); }
        catch (Exception exception)
        {
            TryWriteError($"Extension UI component '{name}' disposal failed: {exception.Message}");
        }
    }

    internal async Task<NativeExtensions.PiSharpExtensionUiCompletion<T>> ShowCustomAsync<T>(Guid ownerId,
        Func<Action<T>, NativeExtensions.IPiSharpExtensionUiComponent> factory,
        NativeExtensions.PiSharpExtensionUiCustomOptions? options, CancellationToken cancellationToken,
        CancellationToken packageLifetime)
    {
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanShowDialog)
            return NativeExtensions.PiSharpExtensionUiCompletion<T>.Cancelled;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
        var request = new NativeExtensionCustomComponentRequest<T>(ownerId, options ?? new(), FinishCustom,
            ReportCustomInputFailure);
        var component = factory(request.Complete);
        ArgumentNullException.ThrowIfNull(component);
        request.Attach(component);
        if (request.IsCompleted) return await request.Completion.ConfigureAwait(false);

        NativeExtensionCustomComponentRequest? replaced;
        bool hasInteractiveScreen;
        lock (_gate)
        {
            hasInteractiveScreen = _interactiveScreen is { IsActive: true } && !linked.IsCancellationRequested;
            if (hasInteractiveScreen)
            {
                replaced = _activeCustom;
                _activeCustom = request;
            }
            else replaced = null;
        }
        if (!hasInteractiveScreen)
        {
            request.Cancel();
            linked.Token.ThrowIfCancellationRequested();
            return await request.Completion.ConfigureAwait(false);
        }
        replaced?.Cancel();
        request.AttachRedraw(QueuePresentationRefresh);
        RefreshPresentation();
        using var cancellation = linked.Token.Register(static state =>
            ((NativeExtensionCustomComponentRequest)state!).Cancel(), request);
        try
        {
            TerminalEditor? editor;
            lock (_gate) editor = _interactiveEditor;
            if (editor?.ShouldRunExtensionModalInputPump == true)
                await editor.RunExtensionModalInputPumpAsync(() => !request.IsCompleted, linked.Token)
                    .ConfigureAwait(false);
            return await request.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { request.Cancel(); }
    }

    private void FinishCustom(NativeExtensionCustomComponentRequest request)
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = ReferenceEquals(_activeCustom, request);
            if (wasActive) _activeCustom = null;
        }
        request.DisposeComponent();
        if (wasActive) RefreshPresentation();
    }

    private void ReportCustomInputFailure(string message)
    {
        TerminalScreen? screen;
        lock (_gate) screen = _interactiveScreen;
        if (screen is not null) ReportRenderFailure(screen, "custom", message);
    }

    internal IDisposable AddTerminalInputListener(Guid owner, long order,
        NativeExtensions.PiSharpExtensionTerminalInputHandler handler)
        => mode == "rpc" ? EmptyRegistration.Instance : _inputRouter.Add(owner, order, handler);

    internal TerminalInputInterception InterceptTerminalInput(TerminalInputEvent input, bool reserved)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.IsEndOfStream)
        {
            NativeExtensionCustomComponentRequest? active;
            lock (_gate) active = _activeCustom;
            if (active is null) return new(false, input);
            active.Cancel();
            return new(true, input);
        }
        if (input.IsControl) return new(false, input);

        NativeExtensionCustomComponentRequest? custom;
        TerminalEditor? editor;
        lock (_gate)
        {
            custom = _activeCustom;
            editor = _interactiveEditor;
        }
        if (reserved)
        {
            if (custom is not null && input.Key?.Key == ConsoleKey.Escape)
            {
                custom.Cancel();
                return new(true, input);
            }
            custom?.Cancel();
            return new(false, input);
        }

        var failed = false;
        var interception = input.IsExtensionRewrite
            ? new TerminalInputInterception(false, input)
            : _inputRouter.Intercept(input, candidate => reserved ||
                editor?.IsReservedExtensionInput(candidate) == true, out failed);
        if (failed)
        {
            TerminalScreen? screen;
            lock (_gate) screen = _interactiveScreen;
            screen?.SetStatusNotification("An extension input handler failed", TerminalStatusNotificationKind.Error);
        }
        if (interception.Consumed) return interception;

        var routed = interception.Input;
        if (editor?.IsReservedExtensionInput(routed) == true)
        {
            custom?.Cancel();
            return interception;
        }
        if (custom is not null)
        {
            if (routed.Key?.Key == ConsoleKey.Escape)
            {
                custom.Cancel();
                return interception with { Consumed = true };
            }
            var hostAction = routed.Key is { } key && editor?.IsHostActionKey(key) == true;
            if (!hostAction && custom.HandleInput(routed)) return interception with { Consumed = true };
        }
        return interception;
    }

    internal void RemoveOwner(Guid owner)
    {
        List<ComponentEntry> removed = [];
        AutocompleteEntry[] removedProviders;
        NativeExtensionCustomComponentRequest? custom = null;
        lock (_gate)
        {
            var statusKeys = _statuses.Keys.Where(key => key.Owner == owner).Select(key => key.Key).Distinct().ToArray();
            var widgetKeys = _widgets.Keys.Where(key => key.Owner == owner).Select(key => key.Key).Distinct().ToArray();
            foreach (var key in _statuses.Keys.Where(key => key.Owner == owner).ToArray())
                _statuses.Remove(key);
            foreach (var key in _widgets.Keys.Where(key => key.Owner == owner).ToArray())
            {
                if (_widgets.Remove(key, out var widget) && widget.Component is not null)
                    removed.Add(widget.Component);
            }
            if (_headers.Remove(owner, out var header)) removed.Add(header.Component);
            if (_footers.Remove(owner, out var footer)) removed.Add(footer.Component);
            _titles.Remove(owner);
            _presentationContributions.Remove(owner);
            if (_editors.Remove(owner, out var editorComponent)) removed.Add(editorComponent.Component);
            removedProviders = _autocompleteProviders.Where(provider => provider.OwnerId == owner).ToArray();
            foreach (var provider in removedProviders) _autocompleteProviders.Remove(provider);
            if (_activeCustom?.OwnerId == owner) custom = _activeCustom;
            _pendingRpcNotifications.RemoveAll(item => item.Owner == owner);
            _pendingRpcEditorUpdates.RemoveAll(item => item.Owner == owner);
            foreach (var key in statusKeys) EmitRpcStatusUnsafe(key);
            foreach (var key in widgetKeys) EmitRpcWidgetUnsafe(key);
        }
        _inputRouter.RemoveOwner(owner);
        foreach (var provider in removedProviders) DisposeAutocompleteProvider(provider);
        foreach (var component in removed) component.Dispose();
        custom?.Cancel();
        UpdateTerminalTitle();
        UpdateWorkingPresentation();
        UpdateHiddenThinkingLabel();
        RefreshPresentation();
    }

    internal void ReportLifecycleFailure(string action, Exception exception) =>
        TryWriteError($"{action}: {exception.Message}");

    private void RefreshPresentation()
    {
        QueuePresentationRefresh();
    }

    private void QueuePresentationRefresh()
    {
        Interlocked.Increment(ref _presentationVersion);
        lock (_gate)
            if (_interactiveScreen is null) return;
        if (Interlocked.Exchange(ref _presentationRefreshQueued, 1) != 0) return;
        SchedulePresentationRefresh();
    }

    private void SchedulePresentationRefresh()
    {
        lock (_gate)
        {
            if (_interactiveScreen is null)
            {
                Interlocked.Exchange(ref _presentationRefreshQueued, 0);
                return;
            }
            _presentationTimer ??= new(static state => ((NativeExtensionInteractiveUiHost)state!).RenderPresentation(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _presentationTimer.Change(TimeSpan.FromMilliseconds(16), Timeout.InfiniteTimeSpan);
        }
    }

    private void RenderPresentation()
    {
        var version = Volatile.Read(ref _presentationVersion);
        try
        {
            TerminalScreen? screen;
            (string Key, string Text)[] statuses;
            KeyValuePair<(Guid Owner, string Key), WidgetEntry>[] widgets;
            KeyValuePair<Guid, (long Order, ComponentEntry Component)>[] headers;
            KeyValuePair<Guid, (long Order, ComponentEntry Component)>[] footers;
            NativeExtensionCustomComponentRequest? custom;
            lock (_gate)
            {
                screen = _interactiveScreen;
                statuses = _statuses.Select(pair => (pair.Key.Key, pair.Value.Text, pair.Value.Order))
                    .OrderBy(item => item.Order).ThenBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => (item.Key, item.Text)).ToArray();
                widgets = _widgets.OrderBy(pair => pair.Value.Order).ThenBy(pair => pair.Key.Key, StringComparer.Ordinal).ToArray();
                headers = _headers.OrderBy(pair => pair.Value.Order).ToArray();
                footers = _footers.OrderBy(pair => pair.Value.Order).ToArray();
                custom = _activeCustom;
            }

            if (screen is not null)
            {
                var statusMap = statuses.GroupBy(status => status.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last().Text, StringComparer.Ordinal);
                var context = screen.CreateExtensionUiRenderContext(statusMap);
                var above = new List<NativeExtensions.PiSharpExtensionUiLine>();
                var below = new List<NativeExtensions.PiSharpExtensionUiLine>();
                foreach (var (key, widget) in widgets)
                {
                    var lines = widget.Lines;
                    if (widget.Component is not null)
                        lines = widget.Component.Render(context, size => ReportRenderFailure(screen, key.Key, size));
                    (widget.Placement == NativeExtensions.PiSharpExtensionWidgetPlacement.AboveEditor ? above : below)
                        .AddRange(lines);
                }
                ComponentEntry? editorComponent;
                lock (_gate) editorComponent = _editors.Count == 0 ? null : _editors.MaxBy(pair => pair.Value.Order).Value.Component;
                IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine>? editorReplacement = null;
                if (editorComponent is not null)
                {
                    var editorLines = editorComponent.Render(context with { IsFocused = true },
                        message => ReportRenderFailure(screen, "editor", message));
                    if (editorComponent.EditorComponent?.ReplacesDefaultEditor == true) editorReplacement = editorLines;
                    else below.AddRange(editorLines);
                }
                var header = headers.LastOrDefault().Value.Component?.Render(context,
                    size => ReportRenderFailure(screen, "header", size)) ?? [];
                var footer = footers.LastOrDefault().Value.Component?.Render(context,
                    size => ReportRenderFailure(screen, "footer", size)) ?? [];
                var customLines = custom?.Render(context, message => ReportRenderFailure(screen, "custom", message));
                var isOverlay = custom?.Options.HostingMode == NativeExtensions.PiSharpExtensionUiHostingMode.Overlay;
                lock (_gate)
                {
                    if (!ReferenceEquals(_interactiveScreen, screen)) return;
                    screen.SetExtensionPresentation(statuses, header, above, below, footer, editorReplacement);
                    screen.SetExtensionCustomPanel(isOverlay ? null : customLines);
                    screen.SetExtensionCustomOverlay(isOverlay ? customLines : null,
                        isOverlay ? custom?.Options.Overlay : null);
                }
            }
        }
        catch (Exception exception)
        {
            TryWriteError($"Extension UI presentation refresh failed: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _presentationRefreshQueued, 0);
            if (version != Volatile.Read(ref _presentationVersion) &&
                Interlocked.Exchange(ref _presentationRefreshQueued, 1) == 0)
                SchedulePresentationRefresh();
        }
    }

    private void StopPresentationRefresh()
    {
        lock (_gate)
        {
            _presentationTimer?.Dispose();
            _presentationTimer = null;
            Interlocked.Exchange(ref _presentationRefreshQueued, 0);
        }
    }

    private void ReportRenderFailure(TerminalScreen screen, string key, string message)
    {
        TryWriteError($"Extension UI component '{key}' failed: {message}");
        try
        {
            screen.SetStatusNotification($"Extension UI component '{key}' failed to render",
                TerminalStatusNotificationKind.Error);
        }
        catch (ObjectDisposedException) { }
    }

    private void TryWriteError(string message)
    {
        try { error.WriteLine(TerminalSafeText.Normalize(message)); }
        catch (Exception) { }
    }

    private static string NormalizeOneLine(string value) =>
        TerminalSafeText.Normalize(value).Replace('\n', ' ');

    private static string NormalizeExtensionKey(string key, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, parameterName);
        var safe = NormalizeOneLine(key).Trim();
        if (safe.Length == 0)
            throw new ArgumentException("Extension UI keys cannot contain only control characters.", parameterName);
        return safe.Length > 256 ? safe[..256] : safe;
    }

    private TerminalScreen CreateScreen(out TerminalEditor editor)
    {
        var screen = new TerminalScreen(output, error, useAlternateScreen: false, deferInitialRender: true);
        screen.Activate();
        editor = new TerminalEditor();
        editor.AttachScreen(screen);
        return screen;
    }

    private sealed class AutocompleteEntry(Guid ownerId, long order, long registrationOrder,
        NativeExtensions.PiSharpExtensionAutocompleteProvider provider) : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private int _active = 1;

        public Guid OwnerId { get; } = ownerId;
        public long Order { get; } = order;
        public long RegistrationOrder { get; } = registrationOrder;
        public NativeExtensions.PiSharpExtensionAutocompleteProvider Provider { get; } = provider;
        public CancellationToken LifetimeToken => _lifetime.Token;
        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            _lifetime.Cancel();
        }
    }

    private sealed class AutocompleteRegistration(NativeExtensionInteractiveUiHost owner, AutocompleteEntry entry) : IDisposable
    {
        private int _active = 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            owner.RemoveAutocompleteProvider(entry);
        }
    }

    private sealed class EmptyRegistration : IDisposable
    {
        internal static EmptyRegistration Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class ScreenBinding(NativeExtensionInteractiveUiHost owner, TerminalScreen screen,
        TerminalEditor? editor, long bindingId) : IDisposable
    {
        private int _active = 1;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;
            NativeExtensionCustomComponentRequest? custom;
            bool isCurrent;
            bool screenIsBound;
            lock (owner._gate)
            {
                screenIsBound = ReferenceEquals(owner._interactiveScreen, screen);
                isCurrent = screenIsBound && owner._interactiveBindingId == bindingId;
                if (isCurrent)
                {
                    owner._interactiveScreen = null;
                    owner._interactiveEditor = null;
                    owner._terminalPresentation = null;
                    owner._interactiveNotificationHandler = null;
                    custom = owner._activeCustom;
                }
                else custom = null;
            }
            if (isCurrent)
            {
                editor?.SetExtensionInputInterceptor(null);
                editor?.SetExtensionEditorInputHandler(null);
                editor?.SetExtensionAutocompleteProvider(null);
                editor?.SetExtensionModalActionHandlers(null, null);
                owner.StopPresentationRefresh();
                custom?.Cancel();
            }
            if (isCurrent || !screenIsBound)
            {
                screen.SetExtensionUiContextChangedHandler(null);
                screen.SetExtensionPresentation([], [], [], [], []);
                screen.SetExtensionCustomPanel(null);
                screen.SetExtensionCustomOverlay(null, null);
                screen.SetExtensionTitle("");
                screen.SetExtensionWorkingPresentation(null, null, "⠋");
                screen.SetHiddenThinkingLabel(null);
            }
            if (isCurrent) owner.UpdateWorkingPresentation();
        }
    }
}

internal readonly record struct TerminalInputInterception(bool Consumed, TerminalInputEvent Input,
    IReadOnlyList<TerminalInputEvent>? Following = null);
