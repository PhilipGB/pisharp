using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

/// <summary>Owns the active-run screen and composes transcript, status, and editor rows.</summary>
public sealed class TerminalScreen : IDisposable
{
    private const int PageScrollOverlap = 4;
    private const int MaxTranscriptScrollOffset = 60_000;
    private readonly object _gate = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly Func<int> _getColumns;
    private readonly Func<int> _getRows;
    private readonly TerminalRenderScheduler _render;
    private readonly FullscreenWheelScrollAccelerator _wheelScroll;
    private readonly TerminalToolTimers _toolTimers;
    private readonly Dictionary<string,
        Func<string, (PiSharpToolRenderView Collapsed, PiSharpToolRenderView Expanded)>> _toolTimerRenderers = new(StringComparer.Ordinal);
    private readonly ITimer _transcriptScrollbarTimer;
    private readonly ITimer _selectionFlashTimer;
    private readonly TerminalTranscriptBuffer _transcript = new();
    private readonly TerminalConversationPresentation _conversationPresentation;
    private readonly TerminalImageRenderer _images;
    private TerminalScreenCompositor _compositor;
    private bool _useAlternateScreen;
    private TerminalTheme _theme;
    private TerminalColorState _terminalColors;
    private TerminalColorQueryController? _terminalColorQuery;
    private TerminalInput? _terminalInput;
    private readonly TerminalKeyboardProtocol _keyboardProtocol = new();
    private readonly TerminalProtocolInputBinding _protocolInput;
    private Func<TerminalColorState, TerminalTheme>? _themeResolver;
    private readonly ScreenWriter _out;
    private readonly ScreenWriter _error;
    private readonly TerminalMouseRouter _mouse = new();
    private TextWriter? _installedOut;
    private TextWriter? _installedError;
    private string _editorText = "";
    private readonly TranscriptSearchController _search = new();
    private int _editorCursor;
    private int? _editorSelectionStart;
    private int? _editorSelectionEnd;
    private IReadOnlyList<EditorCompletionSuggestion> _editorSuggestions = [];
    private IReadOnlyList<(string Key, string Text)> _extensionStatuses = [];
    private IReadOnlyList<PiSharpExtensionUiLine> _extensionHeader = [];
    private IReadOnlyList<PiSharpExtensionUiLine> _extensionWidgetsAbove = [];
    private IReadOnlyList<PiSharpExtensionUiLine> _extensionWidgetsBelow = [];
    private IReadOnlyList<PiSharpExtensionUiLine>? _extensionEditorReplacement;
    private IReadOnlyList<PiSharpExtensionUiLine> _extensionFooter = [];
    private IReadOnlyList<PiSharpExtensionUiLine>? _extensionCustomOverlay;
    private PiSharpExtensionUiOverlayOptions? _extensionCustomOverlayOptions;
    private IReadOnlyList<PiSharpExtensionUiLine>? _extensionCustomPanel;
    private Action? _extensionUiContextChanged;
    private int _extensionUiContextChangeQueued;
    private string? _workingStatus;
    private string _workingFrame = "⠋";
    private bool _workingVisible = true;
    private string? _extensionWorkingMessage;
    private bool? _extensionWorkingVisible;
    private bool _extensionWorkingFrameVerbatim;
    private string _extensionTitle = "";
    private int _scrollOffset;
    private bool _transientScrollbarVisible;
    private string _transcriptBottomKeyLabel = "Ctrl+End";
    private IReadOnlyList<string>? _transcriptSearchPanel;
    private int? _transcriptSearchCursorColumn;
    private int _lastImagePruneRevision = -1;
    private string _footer = "Enter steers · follow-up queues · Escape aborts";
    private readonly TerminalScreenNotifications _notifications;
    private TerminalClipboard? _selectionClipboard;
    private string? _selectionFlash;
    private Func<TerminalTheme, int, bool, string>? _startupHeader;
    private Func<TerminalTheme, int, bool, string>? _startupContent;
    private bool _startupExpanded;
    private IReadOnlyList<string>? _overlay;
    private IReadOnlyList<string>? _editorPanel;
    private int? _panelCursorRow;
    private int? _panelCursorColumn;
    private bool _panelCursorVisible;
    private int _panelBottomMargin = 1;
    private int _editorPaddingX;
    private int _outputPad = 1;
    private int _autocompleteMaxVisible = 5;
    private bool _showHardwareCursor;
    private bool _showTerminalProgress;
    private bool _startupPanelActive;
    private bool _activated;
    private bool _suspended;
    private bool _terminalModeShutdownPrepared;
    private bool _terminalImagesCleaned;
    private bool _terminalPasteDisabled;
    private volatile bool _active = true;
    private int _deferRender;
    private bool _deferInitialRender;
    private bool _reloadProgressRenderDeferred;

    internal event Action<int>? TerminalWidthChanged;

    public TerminalScreen(TextWriter originalOut, TextWriter originalError,
        Func<int>? getColumns = null, Func<int>? getRows = null)
        : this(originalOut, originalError, getColumns, getRows, new TerminalImageRenderer(), TerminalTheme.Default)
    {
    }

    internal TerminalScreen(TextWriter originalOut, TextWriter originalError, bool useAlternateScreen,
        bool deferInitialRender = false)
        : this(originalOut, originalError, null, null, new TerminalImageRenderer(), TerminalTheme.Default,
            queryTerminalColors: false, deferInitialRender: deferInitialRender, useAlternateScreen: useAlternateScreen)
    {
    }

    internal TerminalScreen(TextWriter originalOut, TextWriter originalError,
        Func<int>? getColumns, Func<int>? getRows, TerminalImageRenderer imageRenderer)
        : this(originalOut, originalError, getColumns, getRows, imageRenderer, TerminalTheme.Default)
    {
    }

    internal TerminalScreen(TextWriter originalOut, TextWriter originalError,
        Func<int>? getColumns, Func<int>? getRows, TerminalImageRenderer imageRenderer, TerminalTheme theme)
        : this(originalOut, originalError, getColumns, getRows, imageRenderer, theme, queryTerminalColors: false)
    {
    }

    internal TerminalScreen(TextWriter originalOut, TextWriter originalError,
        Func<int>? getColumns, Func<int>? getRows, TerminalImageRenderer imageRenderer, TerminalTheme theme,
        bool queryTerminalColors, bool followTerminalAppearance = false, bool deferInitialRender = false,
        TimeSpan? minimumRenderInterval = null, TimeProvider? timeProvider = null, bool useAlternateScreen = true)
    {
        ArgumentNullException.ThrowIfNull(originalOut);
        ArgumentNullException.ThrowIfNull(originalError);
        ArgumentNullException.ThrowIfNull(imageRenderer);
        ArgumentNullException.ThrowIfNull(theme);
        _originalOut = originalOut;
        _originalError = originalError;
        _protocolInput = new(HandleTerminalColorResponse, HandleProtocolDeviceAttributes,
            HandleTerminalColorScheme, HandleKeyboardFlags);
        _images = imageRenderer;
        _useAlternateScreen = useAlternateScreen;
        _theme = theme;
        _deferInitialRender = deferInitialRender;
        var renderInterval = minimumRenderInterval ?? TimeSpan.Zero;
        if (renderInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumRenderInterval));
        var clock = timeProvider ?? TimeProvider.System;
        _render = new(_gate, clock, renderInterval,
            () => _active && !_suspended && _deferRender <= 0 && !_deferInitialRender, RenderFrameLocked);
        _transcriptScrollbarTimer = clock.CreateTimer(ExpireTransientScrollbar, null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _selectionFlashTimer = clock.CreateTimer(ExpireSelectionFlash, null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _terminalColors = new(theme.TerminalForeground, theme.TerminalBackground);
        _getColumns = getColumns ?? ReadColumns;
        _getRows = getRows ?? ReadRows;
        _compositor = new(_originalOut, _images, _useAlternateScreen);
        _conversationPresentation = new(_transcript, _images, Columns, () => _theme);
        _wheelScroll = new(clock);
        _toolTimers = new(clock, RefreshToolTimers);
        _notifications = new(_transcript, () => _theme);
        _out = new(this, isError: false);
        _error = new(this, isError: true);
        try
        {
            _originalOut.Write(_keyboardProtocol.Begin() + (_useAlternateScreen
                ? "\u001b[?1049h\u001b[?7l\u001b[?25l" + TerminalMouseMode.Enable
                : "\u001b[?25l"));
            lock (_gate) _render.Request(immediate: true);
            _terminalColorQuery = new(_originalOut, HandleTerminalColorStateChanged, initialColors: _terminalColors);
            _terminalColorQuery.Start(queryTerminalColors, followTerminalAppearance);
        }
        catch
        {
            _originalOut.Write((_useAlternateScreen
                    ? TerminalMouseMode.Disable + "\u001b[?7h"
                    : "") + "\u001b[?25h" + (_useAlternateScreen ? "\u001b[?1049l" : "") +
                _keyboardProtocol.End());
            _originalOut.Flush();
            _active = false;
            _toolTimers.Dispose();
            throw;
        }
    }

    public TextWriter Output => _out;
    public TextWriter Error => _error;
    public bool IsActive => _active;

    internal bool HasActiveWorkingStatus
    {
        get
        {
            lock (_gate) return _active && _workingVisible && _extensionWorkingVisible != false &&
                _workingStatus is not null;
        }
    }
    internal int TerminalWidth => Columns();
    internal int TerminalHeight => Rows();
    internal int TranscriptScrollOffset { get { lock (_gate) return _scrollOffset; } }
    internal TerminalTheme CurrentTheme { get { lock (_gate) return _theme; } }
    internal TerminalColorState CurrentTerminalColors => _terminalColorQuery?.Current ?? _terminalColors;
    internal bool ToolResultsExpanded { get { lock (_gate) return _transcript.IsExpanded; } }
    internal string? SelectedText
    {
        get { lock (_gate) return _mouse.SelectedText; }
    }

    internal void SetSelectionClipboard(TerminalClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        lock (_gate) _selectionClipboard = clipboard;
    }

    internal async Task<bool> CopySelectionToClipboardAsync(CancellationToken cancellationToken = default)
    {
        TerminalClipboard? clipboard;
        string? selectedText;
        lock (_gate)
        {
            clipboard = _selectionClipboard;
            selectedText = _mouse.SelectedText;
        }
        if (clipboard is null || string.IsNullOrEmpty(selectedText)) return false;

        try
        {
            await clipboard.CopyTextAsync(selectedText, cancellationToken).ConfigureAwait(false);
            SetSelectionFlash("Copied!");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetSelectionFlash(error.Message, TimeSpan.FromSeconds(3));
        }
        return true;
    }

    public void Activate()
    {
        lock (_gate)
        {
            if (!_active || _activated) return;
            Console.SetOut(_out);
            Console.SetError(_error);
            _installedOut = Console.Out;
            _installedError = Console.Error;
            _activated = true;
        }
    }

    internal void RenderInitial()
    {
        lock (_gate)
        {
            if (!_active || !_deferInitialRender) return;
            _deferInitialRender = false;
            _render.Request(immediate: true);
        }
    }

    internal void Suspend()
    {
        lock (_gate)
        {
            if (!_active || _suspended) return;
            _render.Cancel();
            _originalOut.Write(_images.HidePlacements() + (_useAlternateScreen
                    ? TerminalMouseMode.Disable + "\u001b[?7h"
                    : "") + "\u001b[?25h" + (_useAlternateScreen ? "\u001b[?1049l" : "") +
                _keyboardProtocol.End());
            _originalOut.Flush();
            _suspended = true;
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (!_active || !_suspended) return;
            _originalOut.Write(_keyboardProtocol.Begin() + (_useAlternateScreen
                ? "\u001b[?1049h\u001b[?7l\u001b[?25l" + TerminalMouseMode.Enable
                : "\u001b[?25l"));
            _suspended = false;
            _render.Request(immediate: true);
        }
    }

    /// <summary>Matches Pi's shutdown order: release Kitty input before screen modes, then paste after leaving alt-screen.</summary>
    internal bool PrepareTerminalModeShutdown()
    {
        lock (_gate)
        {
            if (!_active || !_useAlternateScreen || _suspended) return false;
            if (_terminalModeShutdownPrepared) return true;
            _originalOut.Write(_keyboardProtocol.End());
            _originalOut.Flush();
            _terminalModeShutdownPrepared = true;
            _render.Request(immediate: true);
            return true;
        }
    }

    internal void PresentTranscriptOnNormalScreen()
    {
        lock (_gate)
        {
            if (!_active || !_useAlternateScreen || _suspended) return;

            _render.Flush();
            AttemptWrite(_images.CleanupControlSequence());
            _terminalImagesCleaned = true;
            if (_extensionTitle.Length > 0)
            {
                AttemptWrite("\u001b]0;\u0007");
                _extensionTitle = "";
            }
            AttemptWrite(TerminalMouseMode.Disable + "\u001b[?7h");
            _compositor.LeaveAlternateScreen(_terminalModeShutdownPrepared ? "\u001b[?2004l" : "");
            _terminalPasteDisabled = _terminalModeShutdownPrepared;

            _useAlternateScreen = false;
            _compositor = new(_originalOut, _images, useAlternateScreen: false);
            RenderFrameLocked();
        }

        void AttemptWrite(string value)
        {
            if (value.Length > 0) _originalOut.Write(value);
        }
    }

    public void SetEditor(string text, int cursor, int? selectionStart = null, int? selectionEnd = null,
        IReadOnlyList<EditorCompletionSuggestion>? suggestions = null, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _editorText = text;
            _editorCursor = Math.Clamp(cursor, 0, text.Length);
            _editorSuggestions = suggestions ?? [];
            _editorSelectionStart = null;
            _editorSelectionEnd = null;
            if (selectionStart is { } start && selectionEnd is { } end && start < end)
            {
                var boundedStart = Math.Clamp(start, 0, text.Length);
                var boundedEnd = Math.Clamp(end, 0, text.Length);
                if (boundedStart < boundedEnd)
                {
                    _editorSelectionStart = boundedStart;
                    _editorSelectionEnd = boundedEnd;
                }
            }
            _render.Request(immediate);
        }
    }

    internal void ReplaceTranscript(Action<TerminalScreen> appendHistory)
    {
        ArgumentNullException.ThrowIfNull(appendHistory);
        lock (_gate)
        {
            if (!_active) return;
            if (_render.Pending) _render.Flush();
            var cleanup = _images.CleanupControlSequence();
            if (cleanup.Length > 0) _originalOut.Write(cleanup);
            _transcript.Clear();
            _toolTimers.Dispose();
            _images.Clear();
            _lastImagePruneRevision = -1;
            _search.Clear();
            _mouse.Clear();
            _scrollOffset = 0;
            _transientScrollbarVisible = false;
            _transcriptScrollbarTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _conversationPresentation.Reset();
            _toolTimerRenderers.Clear();
            _overlay = null;
            _deferRender++;
            try { appendHistory(this); }
            finally
            {
                _deferRender--;
                _render.Request();
            }
        }
    }

    internal void MarkHistoryLoaded(bool endsWithAssistantContent)
    {
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.MarkHistoryLoaded(endsWithAssistantContent);
        }
    }

    internal void SetAssistantText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.SetAssistantText(markdown);
            _render.Request();
        }
    }

    internal void SetAssistantThinkingText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.SetAssistantThinkingText(text);
            _render.Request();
        }
    }

    internal void CommitAssistantThinkingText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var safe = TerminalSafeText.Normalize(text);
        if (safe.Length == 0) return;
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.CommitAssistantThinkingText(safe);
            _render.Request();
        }
    }

    internal void AppendAssistantThinkingBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var safe = TerminalSafeText.Normalize(text);
        if (safe.Length == 0) return;
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.AppendThinkingBlock(safe);
            _render.Request();
        }
    }

    internal void SetThinkingBlocksHidden(bool hidden)
    {
        lock (_gate)
        {
            if (!_active) return;
            if (_conversationPresentation.SetThinkingBlocksHidden(hidden)) _render.Request();
        }
    }

    internal void SetHiddenThinkingLabel(string? label)
    {
        lock (_gate)
        {
            if (!_active) return;
            if (_conversationPresentation.SetHiddenThinkingLabel(label?.Replace('\n', ' '))) _render.Request();
        }
    }

    internal bool ThinkingBlocksHidden { get { lock (_gate) return _conversationPresentation.ThinkingBlocksHidden; } }

    internal void CommitAssistantText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.CommitAssistantText(markdown);
            _render.Request();
        }
    }

    internal void AppendThemedStatusLine(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        string Render(TerminalTheme theme) => Environment.NewLine + Environment.NewLine +
            theme.Style("dim", safe);
        lock (_gate)
        {
            if (!_active) return;
            _transcript.AppendThemedStatus(Render(_theme), Render,
                Environment.NewLine + Environment.NewLine + " " + safe);
            _render.Request();
        }
    }

    internal void AppendExtensionError(string path, string error, bool render = true)
    {
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.AppendExtensionError(path, error);
            if (render) _render.Request();
        }
    }

    internal void ShowReloadProgressPanel(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        lock (_gate)
        {
            if (!_active) return;
            var width = Columns();
            var border = _theme.Fg("border") + new string('─', width) + "\u001b[0m";
            var messageLine = " " + _theme.Style("muted", safe);
            messageLine += new string(' ', Math.Max(0, width - TerminalTextLayout.Width(messageLine)));
            _editorPanel = [border, "", messageLine, "", border];
            _panelCursorRow = null;
            _panelCursorColumn = null;
            _panelCursorVisible = false;
            _panelBottomMargin = 1;
            _render.Request(immediate: true);
            _reloadProgressRenderDeferred = true;
            _deferRender++;
        }
    }

    internal void DismissEditorPanelAndAppendThemedStatusLine(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        string Render(TerminalTheme theme) => Environment.NewLine + theme.Style("dim", safe) + Environment.NewLine;
        lock (_gate)
        {
            if (!_active) return;
            ReleaseReloadProgressRenderLocked();
            _editorPanel = null;
            _panelCursorRow = null;
            _panelCursorColumn = null;
            _panelCursorVisible = false;
            _transcript.AppendThemedStatus(Render(_theme), Render,
                Environment.NewLine + " " + safe + Environment.NewLine);
            _render.Request(immediate: true);
        }
    }

    internal void DismissReloadProgressPanel()
    {
        lock (_gate)
        {
            if (!_active) return;
            ReleaseReloadProgressRenderLocked();
            _editorPanel = null;
            _panelCursorRow = null;
            _panelCursorColumn = null;
            _panelCursorVisible = false;
            _render.Request(immediate: true);
        }
    }

    private void ReleaseReloadProgressRenderLocked()
    {
        if (!_reloadProgressRenderDeferred) return;
        _reloadProgressRenderDeferred = false;
        _deferRender--;
    }

    internal void SetTheme(TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        lock (_gate)
        {
            if (!_active) return;
            _theme = theme.WithTerminalColors(_terminalColors.Foreground, _terminalColors.Background);
            _conversationPresentation.ReRender();
            _render.Request();
            QueueExtensionUiContextChanged();
        }
    }

    internal void SetMarkdownCodeBlockIndent(string codeBlockIndent)
    {
        ArgumentNullException.ThrowIfNull(codeBlockIndent);
        lock (_gate)
        {
            if (_conversationPresentation.SetCodeBlockIndent(codeBlockIndent)) _render.Request();
        }
    }

    internal void SetShowHardwareCursor(bool enabled)
    {
        lock (_gate)
        {
            if (!_active) return;
            _showHardwareCursor = enabled;
            _render.Request();
        }
    }

    internal void SetEditorPaddingX(int padding)
    {
        lock (_gate)
        {
            if (!_active) return;
            _editorPaddingX = Math.Clamp(padding, 0, 3);
            _render.Request();
        }
    }

    internal void SetOutputPad(int padding)
    {
        lock (_gate)
        {
            if (!_active) return;
            _outputPad = Math.Clamp(padding, 0, 1);
            _conversationPresentation.SetOutputPadding(_outputPad);
            _render.Request();
        }
    }

    internal void SetAutocompleteMaxVisible(int maximum)
    {
        lock (_gate)
        {
            if (!_active) return;
            _autocompleteMaxVisible = Math.Clamp(maximum, 3, 20);
            _render.Request();
        }
    }

    internal void SetClearOnShrink(bool enabled)
    {
        lock (_gate)
        {
            if (!_active) return;
            _compositor.SetClearOnShrink(enabled);
            _render.Request();
        }
    }

    internal void SetShowTerminalProgress(bool enabled)
    {
        lock (_gate)
        {
            if (!_active) return;
            if (_showTerminalProgress && !enabled) WriteTerminalProgressControl("\u001b]9;4;0\u0007");
            _showTerminalProgress = enabled;
        }
    }

    internal void SetFullscreenWheelScrollLines(FullscreenWheelScrollSetting setting)
    {
        lock (_gate) _wheelScroll.SetSetting(setting);
    }

    internal void ReportTerminalProgress(double? ratio)
    {
        lock (_gate)
        {
            if (!_active || !_showTerminalProgress) return;
            if (ratio is { } value && double.IsFinite(value))
            {
                var percent = Math.Clamp((int)Math.Round(Math.Clamp(value, 0, 1) * 100,
                    MidpointRounding.AwayFromZero), 0, 100);
                WriteTerminalProgressControl($"\u001b]9;4;1;{percent}\u0007");
            }
            else WriteTerminalProgressControl("\u001b]9;4;3\u0007");
        }
    }

    internal void ClearTerminalProgress()
    {
        lock (_gate)
        {
            if (!_active || !_showTerminalProgress) return;
            WriteTerminalProgressControl("\u001b]9;4;0\u0007");
        }
    }

    private void WriteTerminalProgressControl(string value)
    {
        _originalOut.Write(value);
        _originalOut.Flush();
    }

    internal void SetThemeResolver(Func<TerminalColorState, TerminalTheme> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_gate) _themeResolver = resolver;
    }

    internal void AttachTerminalInput(TerminalInput input)
    {
        _terminalInput = input;
        _protocolInput.Attach(input);
    }

    private void HandleKeyboardFlags(int flags)
    {
        lock (_gate)
        {
            if (!_active || _suspended) return;
            _originalOut.Write(_keyboardProtocol.HandleFlags(flags));
            _originalOut.Flush();
        }
    }

    private void HandleProtocolDeviceAttributes()
    {
        lock (_gate)
        {
            var reply = _keyboardProtocol.HandleDeviceAttributes();
            if (_active && !_suspended) _originalOut.Write(reply.Control);
            _originalOut.Flush();
            if (reply.Consumed) return;
        }
        HandleTerminalDeviceAttributes();
    }

    internal void CompleteInitialTerminalColorQuery(TerminalInput input) =>
        _terminalColorQuery?.CompletePendingReplies(input, disableFollowAppearance: false);

    internal void HandleTerminalColorResponse(TerminalColorResponse response)
    {
        _terminalColorQuery?.HandleColorResponse(response);
    }

    internal void HandleTerminalDeviceAttributes() => _terminalColorQuery?.HandleDeviceAttributes();

    internal void HandleTerminalColorScheme(string appearance) => _terminalColorQuery?.HandleAppearanceReport(appearance);

    internal void SetTerminalAppearanceFollowing(bool enabled) => _terminalColorQuery?.SetFollowAppearance(enabled);

    private void HandleTerminalColorStateChanged(TerminalColorState colors)
    {
        lock (_gate)
        {
            if (!_active || colors.SameAs(_terminalColors)) return;
            _terminalColors = colors;
            try
            {
                _theme = _themeResolver?.Invoke(colors) ?? _theme.WithTerminalColors(colors.Foreground, colors.Background);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or FormatException)
            {
                _theme = _theme.WithTerminalColors(colors.Foreground, colors.Background);
            }
            _conversationPresentation.ReRender();
            _render.Request();
        }
    }

    public void SetFooter(string text, bool render = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _footer = TerminalSafeText.Normalize(text);
            if (render) _render.Request();
        }
    }

    internal string FooterText
    {
        get { lock (_gate) return _footer; }
    }

    internal void SetExtensionStatuses(IReadOnlyList<(string Key, string Text)> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        var safe = statuses.Where(item => !string.IsNullOrWhiteSpace(item.Key) &&
                !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => (NormalizeExtensionStatus(item.Key), NormalizeExtensionStatus(item.Text)))
            .ToArray();
        lock (_gate)
        {
            if (!_active) return;
            _extensionStatuses = safe;
            _render.Request();
        }
    }

    internal void SetExtensionPresentation(IReadOnlyList<(string Key, string Text)> statuses,
        IReadOnlyList<PiSharpExtensionUiLine> header,
        IReadOnlyList<PiSharpExtensionUiLine> aboveEditor,
        IReadOnlyList<PiSharpExtensionUiLine> belowEditor,
        IReadOnlyList<PiSharpExtensionUiLine> footer,
        IReadOnlyList<PiSharpExtensionUiLine>? editorReplacement = null)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(aboveEditor);
        ArgumentNullException.ThrowIfNull(belowEditor);
        ArgumentNullException.ThrowIfNull(footer);
        lock (_gate)
        {
            if (!_active) return;
            _extensionStatuses = statuses.Where(item => !string.IsNullOrWhiteSpace(item.Key) &&
                    !string.IsNullOrWhiteSpace(item.Text))
                .Select(item => (NormalizeExtensionStatus(item.Key), NormalizeExtensionStatus(item.Text))).ToArray();
            _extensionHeader = NormalizeExtensionLines(header);
            _extensionWidgetsAbove = NormalizeExtensionLines(aboveEditor);
            _extensionWidgetsBelow = NormalizeExtensionLines(belowEditor);
            _extensionEditorReplacement = editorReplacement is null ? null : NormalizeExtensionLines(editorReplacement);
            _extensionFooter = NormalizeExtensionLines(footer);
            _render.Request(immediate: true);
        }
    }

    internal void SetExtensionCustomOverlay(IReadOnlyList<PiSharpExtensionUiLine>? lines,
        PiSharpExtensionUiOverlayOptions? options)
    {
        lock (_gate)
        {
            if (!_active) return;
            _extensionCustomOverlay = lines is null ? null : NormalizeExtensionLines(lines);
            _extensionCustomOverlayOptions = options;
            _render.Request(immediate: true);
        }
    }

    internal void SetExtensionCustomPanel(IReadOnlyList<PiSharpExtensionUiLine>? lines)
    {
        lock (_gate)
        {
            if (!_active) return;
            _extensionCustomPanel = lines is null ? null : NormalizeExtensionLines(lines);
            _render.Request(immediate: true);
        }
    }

    internal void SetExtensionUiContextChangedHandler(Action? handler)
    {
        lock (_gate) _extensionUiContextChanged = handler;
    }

    internal PiSharpExtensionUiRenderContext CreateExtensionUiRenderContext(
        IReadOnlyDictionary<string, string> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        lock (_gate)
        {
            return new(Columns(), Rows(), new(_theme.Name, CreateExtensionUiThemeStyles(_theme)),
                new Dictionary<string, string>(statuses, StringComparer.Ordinal));
        }
    }

    internal static IReadOnlyDictionary<string, PiSharpExtensionUiThemeStyle> CreateExtensionUiThemeStyles(
        TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var styles = new Dictionary<string, PiSharpExtensionUiThemeStyle>(StringComparer.Ordinal);
        foreach (var token in s_extensionThemeForegroundTokens)
            styles[token] = new(ToHex(theme.GetConcreteColor(token)), null,
                Bold: token is "accent" or "toolTitle");
        foreach (var token in s_extensionThemeBackgroundTokens)
            styles[token] = new(null, ToHex(theme.GetConcreteColor(token)));
        return styles;
    }

    private static readonly string[] s_extensionThemeForegroundTokens =
    ["accent", "border", "success", "error", "warning", "muted", "dim", "text", "thinkingText",
        "toolTitle", "toolOutput", "mdHeading", "mdLink", "mdCode", "mdQuote", "thinkingOff"];
    private static readonly string[] s_extensionThemeBackgroundTokens =
    ["selectedBg", "searchMatchBg", "userMessageBg", "customMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg"];

    private static string ToHex(TerminalTheme.Rgb color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    private static IReadOnlyList<PiSharpExtensionUiLine> NormalizeExtensionLines(
        IReadOnlyList<PiSharpExtensionUiLine> lines) => lines.Take(200)
        .Select(line => new PiSharpExtensionUiLine(TerminalSafeText.Normalize(line.Text ?? string.Empty),
            line.Style is { Length: <= 32 } style && style.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                ? style : null)).ToArray();

    private static string NormalizeExtensionStatus(string value) =>
        TerminalSafeText.Normalize(value).Replace('\n', ' ');

    private void QueueExtensionUiContextChanged()
    {
        if (_extensionUiContextChanged is null || Interlocked.Exchange(ref _extensionUiContextChangeQueued, 1) != 0)
            return;
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var screen = (TerminalScreen)state!;
            Interlocked.Exchange(ref screen._extensionUiContextChangeQueued, 0);
            try { screen._extensionUiContextChanged?.Invoke(); }
            catch (Exception error) { Console.Error.WriteLine($"Extension UI redraw callback failed: {error.Message}"); }
        }, this);
    }

    internal void SetWorkingStatus(string? status, bool render = true)
    {
        var safeStatus = string.IsNullOrWhiteSpace(status) ? null :
            TerminalSafeText.Normalize(status).Replace('\n', ' ');
        bool changed;
        lock (_gate)
        {
            if (!_active) return;
            changed = _workingStatus != safeStatus;
            _workingStatus = safeStatus;
            if (render) _render.Request();
        }
        if (changed) QueueExtensionUiContextChanged();
    }

    internal void SetWorkingVisible(bool visible)
    {
        lock (_gate)
        {
            if (!_active || _workingVisible == visible) return;
            _workingVisible = visible;
            _render.Request();
        }
    }

    internal void SetWorkingFrame(string frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (!_active) return;
            _workingFrame = TerminalSafeText.Normalize(frame).Replace('\n', ' ');
            _render.Request();
        }
    }

    internal void SetExtensionWorkingPresentation(string? message, bool? visible, string frame,
        bool frameVerbatim = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (!_active) return;
            _extensionWorkingMessage = string.IsNullOrWhiteSpace(message) ? null :
                TerminalSafeText.Normalize(message).Replace('\n', ' ');
            _extensionWorkingVisible = visible;
            _extensionWorkingFrameVerbatim = frameVerbatim;
            _workingFrame = TerminalSafeText.Normalize(frame).Replace('\n', ' ');
            _render.Request();
        }
    }

    internal void SetExtensionTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var safe = TerminalSafeText.Normalize(title).Replace('\n', ' ');
        if (safe.Length > 256) safe = safe[..256];
        lock (_gate)
        {
            if (!_active) return;
            _extensionTitle = safe;
            _originalOut.Write("\u001b]0;" + safe + "\u0007");
            _originalOut.Flush();
        }
    }

    internal void SetStartupHeader(Func<TerminalTheme, int, bool, string> renderHeader)
    {
        ArgumentNullException.ThrowIfNull(renderHeader);
        lock (_gate)
        {
            if (!_active) return;
            _startupHeader = renderHeader;
            _render.Request();
        }
    }

    internal void SetStartupContent(Func<TerminalTheme, int, bool, string> renderContent)
    {
        ArgumentNullException.ThrowIfNull(renderContent);
        lock (_gate)
        {
            if (!_active) return;
            _startupContent = renderContent;
            _render.Request();
        }
    }

    internal void SetOverlay(IReadOnlyList<string>? lines)
    {
        lock (_gate)
        {
            if (!_active) return;
            var opening = lines is not null && _overlay is null;
            if (opening && _render.Pending)
                _render.Flush();
            // List state sanitizes user text before applying application-owned styling.
            _overlay = lines?.Select(line => line.Replace('\n', ' ')).ToArray();
            _render.Request(immediate: opening);
        }
    }

    internal void SetEditorPanel(IReadOnlyList<string>? lines, int? cursorRow = null,
        int? cursorColumn = null, bool cursorVisible = false, int bottomMargin = 1,
        bool flushPendingRenderOnOpen = true, bool renderImmediately = false)
    {
        lock (_gate)
        {
            if (!_active) return;
            var opening = lines is not null && _editorPanel is null;
            if (opening && _render.Pending && flushPendingRenderOnOpen)
                _render.Flush();
            _editorPanel = lines?.Select(line => line.Replace('\n', ' ')).ToArray();
            _panelCursorRow = cursorRow;
            _panelCursorColumn = cursorColumn;
            _panelCursorVisible = cursorVisible;
            _panelBottomMargin = bottomMargin;
            _render.Request(immediate: opening || renderImmediately);
        }
    }

    internal void SetStartupPanel(IReadOnlyList<string> lines, int? cursorRow = null,
        int? cursorColumn = null, bool cursorVisible = false, bool renderImmediately = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
        {
            if (!_active) return;
            _startupPanelActive = true;
            _editorPanel = lines.Select(line => line.Replace('\n', ' ')).ToArray();
            _panelCursorRow = cursorRow;
            _panelCursorColumn = cursorColumn;
            _panelCursorVisible = cursorVisible;
            _render.Request(immediate: renderImmediately || !_render.HasRendered);
        }
    }

    internal void SetStatusNotification(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_gate)
        {
            if (!_active) return;
            var safe = TerminalSafeText.Normalize(message);
            _notifications.PublishStatus(safe, kind, isExtension: false);
            _render.Request();
        }
    }

    internal void AppendStatusMessage(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        lock (_gate)
        {
            if (!_active) return;
            _notifications.PublishStatus(safe, kind, isExtension: true);
            _render.Request();
        }
    }

    internal void ShowModelChangeStatus(string footer, string message)
    {
        ArgumentNullException.ThrowIfNull(footer);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        lock (_gate)
        {
            if (!_active) return;
            _editorPanel = null;
            _panelCursorRow = null;
            _panelCursorColumn = null;
            _panelCursorVisible = false;
            _footer = TerminalSafeText.Normalize(footer);
            _notifications.PublishStatus(safe, TerminalStatusNotificationKind.Info, isExtension: true);
            _render.Request();
        }
    }

    internal void DismissEditorPanelAndAppendStatus(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        lock (_gate)
        {
            if (!_active) return;
            _editorPanel = null;
            _panelCursorRow = null;
            _panelCursorColumn = null;
            _panelCursorVisible = false;
            _notifications.PublishStatus(safe, kind, isExtension: true);
            _render.Request();
        }
    }

    public void RefreshIfResized()
    {
        lock (_gate)
        {
            if (!_active) return;
            var columns = Columns();
            var rows = Rows();
            if (columns != _lastColumns || rows != _lastRows) QueueExtensionUiContextChanged();
            if (columns != _lastColumns || rows != _lastRows) _render.Request(immediate: true);
        }
    }

    internal void ScrollPage(bool up)
    {
        lock (_gate)
        {
            if (!_active) return;
            var transcriptHeight = _lastTranscriptHeight > 0 ? _lastTranscriptHeight : _lastRows;
            var pageRows = Math.Max(1, transcriptHeight - PageScrollOverlap);
            SetScrollOffsetLocked(_scrollOffset + (up ? pageRows : -pageRows));
        }
    }

    internal void ScrollToTop()
    {
        lock (_gate)
        {
            if (!_active) return;
            SetScrollOffsetLocked(MaxTranscriptScrollOffset);
        }
    }

    internal void ScrollToBottom()
    {
        lock (_gate)
        {
            if (!_active || _scrollOffset == 0) return;
            _scrollOffset = 0;
            ShowTransientScrollbarLocked();
            _render.Request();
        }
    }

    internal TerminalMouseResult HandleMouse(TerminalMouseEvent mouse)
    {
        lock (_gate)
        {
            if (!_active || _suspended) return default;
            if (mouse.IsWheel)
            {
                var direction = mouse.WheelDirection;
                var delta = direction == 0 ? 0 : -direction *
                    _wheelScroll.Next(direction) * mouse.WheelScrollMultiplier;
                if (delta != 0) SetScrollOffsetLocked(_scrollOffset + delta);
                return new(delta != 0);
            }
            var result = _mouse.Handle(mouse);
            if (result.Changed) _render.Request();
            return result;
        }
    }

    internal TranscriptSearchState SearchTranscript(string query, int direction = 0)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = TerminalSafeText.Normalize(query);
        lock (_gate)
        {
            if (!_active) return default;
            _search.SetQuery(query, direction);
            _ = _search.Highlight(GetTranscriptTextLocked(Columns()), _theme);
            _render.Request();
            return _search.State;
        }
    }

    internal void ClearTranscriptSearch()
    {
        lock (_gate)
        {
            if (!_active || _search.Query.Length == 0) return;
            _search.Clear();
            _render.Request();
        }
    }

    internal void AppendUserMessage(string text, IReadOnlyList<DataContent>? images = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var safe = TerminalSafeText.Normalize(text);
        lock (_gate)
        {
            if (!_active) return;
            if (_conversationPresentation.AppendUserMessage(safe, images)) _render.Request();
        }
    }

    internal void SetToolExpandKeyLabel(string? keyLabel)
    {
        var safe = TerminalSafeText.Normalize(keyLabel ?? string.Empty);
        lock (_gate)
        {
            if (_conversationPresentation.SetToolExpandKeyLabel(safe)) _render.Request();
        }
    }

    internal void SetTranscriptBottomKeyLabel(string? keyLabel)
    {
        var safe = TerminalSafeText.Normalize(string.IsNullOrWhiteSpace(keyLabel) ? "" : keyLabel);
        lock (_gate)
        {
            if (_transcriptBottomKeyLabel == safe) return;
            _transcriptBottomKeyLabel = safe;
            _render.Request();
        }
    }

    internal void SetTranscriptSearchPanel(IReadOnlyList<string>? lines, int? cursorColumn = null)
    {
        var safe = lines?.ToArray();
        lock (_gate)
        {
            if (!_active) return;
            _transcriptSearchPanel = safe;
            _transcriptSearchCursorColumn = safe is null ? null : cursorColumn;
            _render.Request(immediate: safe is not null);
        }
    }

    internal string? ActiveAuthorizationUrl
    {
        get { lock (_gate) return _notifications.AuthorizationUrl; }
    }

    internal void ClearMcpAuthorizationNotification(string serverName)
    {
        ClearAuthorizationUrl("mcp:" + serverName);
    }

    internal void ClearAuthorizationUrl(string owner)
    {
        lock (_gate)
        {
            _notifications.ClearAuthorization(owner);
        }
    }

    internal bool SetAuthorizationUrlCopyStatus(string message, TerminalStatusNotificationKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_gate)
        {
            if (!_active || _notifications.AuthorizationUrl is null || _editorPanel is null) return false;
            _notifications.SetCopyStatus(TerminalSafeText.Normalize(message), kind);
            _render.Request();
            return true;
        }
    }

    internal void AppendMcpAuthorizationNotification(string serverName, Uri authorizationUrl,
        string? copyKeyLabel = null, string? cancelKeyLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        var safeName = TerminalSafeText.Normalize(serverName);
        AppendAuthorizationUrlNotification("mcp:" + serverName,
            "Sign in to MCP server \"" + safeName + "\" in your browser:",
            "If the browser cannot reach this terminal, paste the redirect URL and press Enter here.", authorizationUrl,
            copyKeyLabel, cancelKeyLabel);
    }

    internal bool AppendProviderAuthorizationNotification(string providerId, string providerName,
        string message, Uri authorizationUrl, string? copyKeyLabel, string? cancelKeyLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return AppendAuthorizationUrlNotification("provider:" + providerId,
            "Login to " + TerminalSafeText.Normalize(providerName) + ":",
            TerminalSafeText.Normalize(message), authorizationUrl, copyKeyLabel, cancelKeyLabel);
    }

    private bool AppendAuthorizationUrlNotification(string owner, string heading, string? message,
        Uri authorizationUrl, string? copyKeyLabel, string? cancelKeyLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(authorizationUrl);
        if (!authorizationUrl.IsAbsoluteUri ||
            !(authorizationUrl.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              authorizationUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrEmpty(authorizationUrl.Host))
            throw new ArgumentException("Authorization URL must use HTTP or HTTPS.", nameof(authorizationUrl));

        var target = authorizationUrl.AbsoluteUri;
        var notification = _notifications.RenderAuthorizationUrl(target, heading, message,
            copyKeyLabel, cancelKeyLabel);
        lock (_gate)
        {
            if (!_active) return false;
            _notifications.PublishAuthorization(owner, target, notification);
            _render.Request();
            return true;
        }
    }

    internal void AppendToolResult(string text, IReadOnlyList<DataContent>? images = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var safe = TerminalSafeText.Normalize(text);
        lock (_gate)
        {
            if (!_active) return;
            if (_conversationPresentation.AppendToolResult(safe, images)) _render.Request();
        }
    }

    internal void AppendToolCall(PiSharpToolRenderView view, string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.AppendToolCall(view, operationId);
            _render.Request();
        }
    }

    internal void AppendToolCall(PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.AppendToolCall(collapsed, expanded, operationId);
            _render.Request();
        }
    }

    internal void AppendToolResult(PiSharpToolRenderView view, IReadOnlyList<DataContent>? images = null) =>
        AppendToolResult(view, view, images);

    internal void StartToolTimer(string id)
    {
        lock (_gate) if (_active) _toolTimers.Start(id);
    }

    internal void UpdateToolTimer(string id, bool isError)
    {
        lock (_gate)
        {
            if (!_active || _toolTimers.Progress(id, isError) is not { } text) return;
            SetElapsedRow(id, text);
            _render.Request();
        }
    }

    internal string? ProgressToolTimerInOutput(string id)
    {
        lock (_gate)
            return _active ? _toolTimers.Progress(id, isError: false, attachedToOutput: true) : null;
    }

    internal double? CompleteToolTimer(string id, double? durationMs)
    {
        lock (_gate)
        {
            _toolTimerRenderers.Remove(id);
            return _toolTimers.Complete(id, durationMs);
        }
    }

    internal void SetToolTimerRenderer(string id,
        Func<string, (PiSharpToolRenderView Collapsed, PiSharpToolRenderView Expanded)> render)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(render);
        lock (_gate)
            if (_active) _toolTimerRenderers[id] = render;
    }

    internal void CompleteToolRow(string id, PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        bool isError, IReadOnlyList<DataContent>? images)
    {
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.SetToolResult(id, collapsed, expanded, isError, finished: true, images);
            _toolTimerRenderers.Remove(id);
            _render.Request();
        }
    }

    internal void UpdateToolRow(string id, PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        bool isError, IReadOnlyList<DataContent>? images = null)
    {
        lock (_gate)
        {
            if (!_active) return;
            _conversationPresentation.SetToolResult(id, collapsed, expanded, isError, finished: false, images);
            _render.Request();
        }
    }

    internal void ClearToolTimers()
    {
        List<(string Id, double Duration, Func<string,
            (PiSharpToolRenderView Collapsed, PiSharpToolRenderView Expanded)>? Render)> rows;
        lock (_gate)
        {
            var cancelled = _toolTimers.Cancel();
            if (!_active || cancelled.Count == 0) return;
            rows = cancelled.Select(row =>
            {
                _toolTimerRenderers.Remove(row.Id, out var render);
                return (Id: row.Id, Duration: row.DurationMs, Render: render);
            }).ToList();
        }

        var rendered = rows.Select(row =>
        {
            var text = "Took " + ToolDurationText.Format(row.Duration);
            return (row.Id, Text: text, Views: row.Render?.Invoke(text));
        }).ToArray();
        lock (_gate)
        {
            if (!_active) return;
            foreach (var row in rendered)
            {
                if (row.Views is { } views)
                    _conversationPresentation.SetToolResult(row.Id, views.Collapsed, views.Expanded,
                        isError: true, finished: true);
                else
                {
                    var view = PiSharpToolRenderView.FromText(row.Text, PiSharpToolTextStyle.Muted);
                    _conversationPresentation.SetToolResult(row.Id, view, view, isError: true, finished: true);
                }
            }
            _render.Request();
        }
    }

    private void RefreshToolTimers()
    {
        List<(string Id, string Text,
            Func<string, (PiSharpToolRenderView Collapsed, PiSharpToolRenderView Expanded)> Render)> renderers = [];
        lock (_gate)
        {
            if (!_active) return;
            var rows = _toolTimers.Rows();
            if (rows.Count == 0) return;
            foreach (var (id, text, attachedToOutput) in rows)
            {
                if (attachedToOutput && _toolTimerRenderers.TryGetValue(id, out var render))
                    renderers.Add((id, text, render));
                else if (!attachedToOutput) SetElapsedRow(id, text);
            }
            if (renderers.Count == 0) _render.Request();
        }

        var rendered = renderers.Select(row => (row.Id, row.Render,
            Views: row.Render(row.Text))).ToArray();
        lock (_gate)
        {
            if (!_active) return;
            foreach (var row in rendered)
            {
                if (!_toolTimerRenderers.TryGetValue(row.Id, out var current) || current != row.Render) continue;
                _conversationPresentation.SetToolResult(row.Id, row.Views.Collapsed, row.Views.Expanded,
                    isError: false, finished: false);
            }
            _render.Request();
        }
    }

    private void SetElapsedRow(string id, string text)
    {
        var view = PiSharpToolRenderView.FromText(text, PiSharpToolTextStyle.Muted);
        _conversationPresentation.SetToolResult(id, view, view, isError: false, finished: false);
    }

    internal void AppendToolResult(PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        IReadOnlyList<DataContent>? images = null, bool suppressUnsupportedImageFallbackWhenCollapsed = false,
        string? operationId = null, bool isError = false)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        lock (_gate)
        {
            if (!_active) return;
            if (_conversationPresentation.AppendToolResult(collapsed, expanded, images,
                    suppressUnsupportedImageFallbackWhenCollapsed, operationId, isError)) _render.Request();
        }
    }

    internal bool ToggleToolResultsExpanded()
    {
        lock (_gate)
        {
            if (!_active) return _transcript.IsExpanded;
            var expanded = _transcript.ToggleExpanded();
            _startupExpanded = expanded;
            _render.Request();
            return expanded;
        }
    }

    internal void SetToolResultsExpanded(bool expanded)
    {
        lock (_gate)
        {
            if (!_active) return;
            var changed = _transcript.IsExpanded != expanded || _startupExpanded != expanded;
            _transcript.SetExpanded(expanded);
            _startupExpanded = expanded;
            if (changed) _render.Request();
        }
    }

    private void SetScrollOffsetLocked(int value)
    {
        _scrollOffset = Math.Clamp(value, 0, MaxTranscriptScrollOffset);
        ShowTransientScrollbarLocked();
        _render.Request();
    }

    private void ShowTransientScrollbarLocked()
    {
        _transientScrollbarVisible = true;
        _transcriptScrollbarTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private void ExpireTransientScrollbar(object? state)
    {
        lock (_gate)
        {
            if (!_active || !_transientScrollbarVisible) return;
            _transientScrollbarVisible = false;
            if (_scrollOffset == 0) _render.Request();
        }
    }

    private void SetSelectionFlash(string message, TimeSpan? duration = null)
    {
        lock (_gate)
        {
            if (!_active) return;
            _selectionFlash = TerminalSafeText.Normalize(message);
            _selectionFlashTimer.Change(duration ?? TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            _render.Request(immediate: true);
        }
    }

    private void ExpireSelectionFlash(object? state)
    {
        lock (_gate)
        {
            if (!_active || _selectionFlash is null) return;
            _selectionFlash = null;
            _render.Request();
        }
    }

    internal void WriteControl(string value)
    {
        lock (_gate)
        {
            if (_active) _originalOut.Write(value);
        }
    }

    public void Dispose()
    {
        Exception? failure = null;

        void Attempt(Action action)
        {
            try { action(); }
            catch (Exception error) { failure ??= error; }
        }

        Attempt(() =>
        {
            if (Console.IsInputRedirected || _terminalColorQuery is not { HasPendingReplies: true } query) return;
            var input = _terminalInput ?? TerminalInput.OpenConsole();
            using var mode = TerminalMode.Enter(this);
            AttachTerminalInput(input);
            query.CompletePendingReplies(input);
        });
        Attempt(() => _terminalColorQuery?.Dispose());
        Attempt(_protocolInput.Dispose);

        var truncated = false;
        var terminalModeShutdownPrepared = false;
        var wasActive = false;
        lock (_gate)
        {
            if (!_active)
            {
                wasActive = false;
            }
            else
            {
                wasActive = true;
                Attempt(_render.Flush);
                Attempt(_render.Dispose);
                Attempt(_transcriptScrollbarTimer.Dispose);
                Attempt(_selectionFlashTimer.Dispose);
                Attempt(_toolTimers.Dispose);
                _active = false;
                terminalModeShutdownPrepared = _terminalModeShutdownPrepared;
                if (_activated)
                {
                    Attempt(() =>
                    {
                        if (ReferenceEquals(Console.Out, _installedOut)) Console.SetOut(_originalOut);
                    });
                    Attempt(() =>
                    {
                        if (ReferenceEquals(Console.Error, _installedError)) Console.SetError(_originalError);
                    });
                }

                Attempt(() =>
                {
                    var cleanup = _terminalImagesCleaned ? "" : _images.CleanupControlSequence();
                    _terminalImagesCleaned = true;
                    _originalOut.Write((_useAlternateScreen ? "\u001b[?2026h" : "") + cleanup);
                });
                if (_extensionTitle.Length > 0) Attempt(() => _originalOut.Write("\u001b]0;\u0007"));
                if (_useAlternateScreen)
                {
                    Attempt(() => _originalOut.Write(TerminalMouseMode.Disable));
                    Attempt(() => _originalOut.Write("\u001b[?7h\u001b[?2026l"));
                }
                Attempt(_originalOut.Flush);
                if (!_useAlternateScreen) Attempt(() => _originalOut.Write("\u001b[?25h"));
                if (!terminalModeShutdownPrepared) Attempt(() => _originalOut.Write(_keyboardProtocol.End()));
                Attempt(_originalOut.Flush);
                Attempt(() =>
                {
                    if (_useAlternateScreen)
                    {
                        _compositor.LeaveAlternateScreen(
                            terminalModeShutdownPrepared ? "\u001b[?2004l" : "");
                        _compositor.RestoreLastFrameToNormalBuffer();
                    }
                    else if (terminalModeShutdownPrepared && !_terminalPasteDisabled)
                        _originalOut.Write("\u001b[?2004l");
                });

                if (!_useAlternateScreen)
                {
                    if (terminalModeShutdownPrepared) Attempt(() => _originalOut.Write("\u001b[?2004l"));
                    Attempt(() => _originalOut.Write("\u001b[?25h"));
                    Attempt(_originalOut.Flush);
                }

                truncated = _transcript.CaptureTruncated;
            }
        }

        if (!wasActive)
        {
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }

        if (truncated && !_useAlternateScreen)
        {
            Attempt(() => _originalError.WriteLine("Interactive transcript exceeded the scrollback restore limit; the active screen was still rendered in full."));
            Attempt(_originalError.Flush);
        }
        if (!_useAlternateScreen) Attempt(_compositor.LeaveMainScreen);
        Attempt(_images.Clear);

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private int _lastColumns;
    private int _lastRows;
    private int _lastTranscriptHeight;
    private void Append(string value, bool isError)
    {
        var safe = TerminalSafeText.Normalize(value);
        lock (_gate)
        {
            if (!_active || safe.Length == 0) return;
            _transcript.Append(safe, isError);
            _render.Request();
        }
    }

    private string GetTranscriptTextLocked(int width) => _transcript.GetText(width) + _conversationPresentation.LiveText;

    private void RenderFrameLocked()
    {
        var columns = Columns();
        var rows = Rows();
        if (columns != _lastColumns || rows != _lastRows) QueueExtensionUiContextChanged();
        if (_lastColumns != 0 && _lastColumns != columns)
        {
            _conversationPresentation.ReRenderMarkdown();
            _transcript.ReRenderThemedSegments(_theme);
            TerminalWidthChanged?.Invoke(columns);
        }
        if (_startupPanelActive)
        {
            var panel = _editorPanel ?? [];
            var frameRows = Enumerable.Repeat("", rows).ToArray();
            for (var index = 0; index < Math.Min(panel.Count, frameRows.Length); index++)
                frameRows[index] = TerminalTranscriptViewport.Clip(panel[index], columns);
            var cursorRow = Math.Clamp(_panelCursorRow ?? 0, 0, Math.Max(0, rows - 1));
            var cursorColumn = Math.Clamp(_panelCursorColumn ?? 1, 1, columns + 1);
            _compositor.Render(new(frameRows, cursorRow, cursorColumn, 0, columns, rows, _panelCursorVisible,
                RestorationRows: frameRows, TranscriptStartRow: 0, TranscriptHeight: rows,
                RestorationCursorRow: cursorRow, DocumentCursorRow: cursorRow,
                DocumentCursorColumn: cursorColumn));
            _lastColumns = columns;
            _lastRows = rows;
            return;
        }
        var footerHeight = rows >= 5 ? 2 : rows > 2 ? 1 : 0;
        var borderHeight = rows - footerHeight >= 4 ? 2 : 0;
        var maxEditorLines = Math.Max(1, Math.Min((int)(rows * 0.3), rows - footerHeight - borderHeight - 1));
        var editor = EditorViewport.Layout(_editorText, _editorCursor, columns, maxEditorLines, showPrompt: false);
        var editorHeight = editor.Rows.Count + borderHeight;
        if (rows - editorHeight - footerHeight < 1 && borderHeight > 0)
        {
            borderHeight = 0;
            editorHeight = editor.Rows.Count;
        }
        var transcriptHeight = Math.Max(1, rows - editorHeight - footerHeight);
        if (_lastImagePruneRevision != _transcript.Revision)
        {
            _images.PruneUnreferenced(_transcript.GetRetainedText());
            _lastImagePruneRevision = _transcript.Revision;
        }
        var transcriptWidth = Math.Max(1, columns);
        var transcript = _images.LayoutTranscript(GetTranscriptTextLocked(transcriptWidth),
            transcriptWidth, Math.Max(1, transcriptHeight - 2));
        var frame = _compositor.Compose(_editorText, _editorCursor, _editorSelectionStart, _editorSelectionEnd,
            transcript, _footer, _overlay, _scrollOffset, columns, rows, _search, _mouse, _theme,
            _editorPanel, _panelCursorRow, _panelCursorColumn, _panelCursorVisible, _panelBottomMargin,
            _notifications.Status, _notifications.StatusKind, _notifications.StatusIsExtension,
            _startupHeader?.Invoke(_theme, columns, _startupExpanded),
            _startupContent?.Invoke(_theme, columns, _startupExpanded), _editorSuggestions,
            !_workingVisible || _extensionWorkingVisible == false || _workingStatus is null
                ? null : _extensionWorkingMessage ?? _workingStatus,
            _notifications.CopyStatus, _notifications.CopyStatusKind,
            _editorPaddingX, _outputPad, _autocompleteMaxVisible, _showHardwareCursor, _extensionStatuses,
            _extensionHeader, _extensionWidgetsAbove, _extensionWidgetsBelow, _extensionFooter,
            _workingVisible && _extensionWorkingVisible != false ? _workingFrame : null,
            _extensionCustomOverlay, _extensionCustomOverlayOptions,
            _extensionCustomPanel ?? _extensionEditorReplacement, _extensionWorkingFrameVerbatim,
            _transcriptBottomKeyLabel, _transcriptSearchPanel, _transientScrollbarVisible,
            _transcriptSearchCursorColumn, _selectionFlash);
        if (frame.ScrollOffset != _scrollOffset) ShowTransientScrollbarLocked();
        _scrollOffset = frame.ScrollOffset;
        _lastColumns = frame.Columns;
        _lastRows = frame.Height;
        _lastTranscriptHeight = frame.TranscriptHeight;
        _compositor.Render(frame);
    }

    private int Columns()
    {
        try { return Math.Clamp(_getColumns(), 20, 400); }
        catch (IOException) { return 80; }
    }

    private int Rows()
    {
        try { return Math.Clamp(_getRows(), 3, 200); }
        catch (IOException) { return 24; }
    }

    internal static int ReadColumns()
    {
        if (!OperatingSystem.IsWindows())
            return TryReadTerminalSize(out var size) ? size.Columns : 80;
        try { return Console.WindowWidth; }
        catch (IOException) { return 80; }
    }

    internal static int ReadRows()
    {
        if (!OperatingSystem.IsWindows())
            return TryReadTerminalSize(out var size) ? size.Rows : 24;
        try { return Console.WindowHeight; }
        catch (IOException) { return 24; }
    }

    private static bool TryReadTerminalSize(out TerminalWindowSize size)
    {
        size = default;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        var request = OperatingSystem.IsMacOS() ? (nuint)0x40087468 : 0x5413u;
        return HasSize(0, out size) || HasSize(1, out size) || HasSize(2, out size);

        bool HasSize(int fileDescriptor, out TerminalWindowSize result)
        {
            if (Ioctl(fileDescriptor, request, out result) != 0 || result.Columns == 0 || result.Rows == 0)
                return false;
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TerminalWindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int Ioctl(int fileDescriptor, nuint request, out TerminalWindowSize size);

    private sealed class ScreenWriter(TerminalScreen screen, bool isError) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => screen.Append(value.ToString(), isError);
        public override void Write(string? value)
        {
            if (value is not null) screen.Append(value, isError);
        }
        public override void Write(char[] buffer, int index, int count) => screen.Append(new string(buffer, index, count), isError);
        public override void WriteLine() => screen.Append(Environment.NewLine, isError);
        public override void WriteLine(string? value) => screen.Append((value ?? "") + Environment.NewLine, isError);
        public override void Flush() { }
    }

}
