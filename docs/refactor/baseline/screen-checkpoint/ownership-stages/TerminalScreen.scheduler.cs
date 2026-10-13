using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

/// <summary>Owns the active-run screen and composes transcript, status, and editor rows.</summary>
public sealed class TerminalScreen : IDisposable
{
    private const int MaxTranscriptScrollOffset = 60_000;
    private readonly object _gate = new();
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly Func<int> _getColumns;
    private readonly Func<int> _getRows;
    private readonly TerminalRenderScheduler _render;
    private readonly TerminalTranscriptBuffer _transcript = new();
    private readonly TerminalImageRenderer _images;
    private readonly TerminalScreenCompositor _compositor;
    private readonly bool _useAlternateScreen;
    private TerminalTheme _theme;
    private string _markdownCodeBlockIndent = "  ";
    private string _toolExpandKeyLabel = "Ctrl+O";
    private TerminalColorState _terminalColors;
    private TerminalColorQueryController? _terminalColorQuery;
    private TerminalInput? _terminalInput;
    private Func<TerminalColorState, TerminalTheme>? _themeResolver;
    private readonly ScreenWriter _out;
    private readonly ScreenWriter _error;
    private readonly TerminalMouseRouter _mouse = new();
    private TextWriter? _installedOut;
    private TextWriter? _installedError;
    private string _editorText = "";
    private string _liveAssistant = "";
    private string _liveAssistantSource = "";
    private string _liveThinking = "";
    private string _liveThinkingSource = "";
    private bool _thinkingBlocksHidden;
    private bool _hasConversationContent;
    private readonly TranscriptSearchController _search = new();
    private int _editorCursor;
    private int? _editorSelectionStart;
    private int? _editorSelectionEnd;
    private IReadOnlyList<EditorCompletionSuggestion> _editorSuggestions = [];
    private string? _workingStatus;
    private int _scrollOffset;
    private int _lastImagePruneRevision = -1;
    private string _footer = "Enter steers · follow-up queues · Escape aborts";
    private string? _statusNotification;
    private TerminalStatusNotificationKind _statusNotificationKind;
    private bool _statusNotificationIsExtension;
    private string? _authorizationUrlOwner;
    private string? _authorizationUrl;
    private string? _authorizationUrlCopyStatus;
    private TerminalStatusNotificationKind _authorizationUrlCopyStatusKind;
    private Func<TerminalTheme, int, bool, string>? _startupHeader;
    private Func<TerminalTheme, int, bool, string>? _startupContent;
    private bool _startupExpanded;
    private IReadOnlyList<string>? _overlay;
    private IReadOnlyList<string>? _editorPanel;
    private int? _panelCursorRow;
    private int? _panelCursorColumn;
    private bool _panelCursorVisible;
    private int _panelBottomMargin = 1;
    private bool _startupPanelActive;
    private bool _activated;
    private bool _suspended;
    private bool _terminalModeShutdownPrepared;
    private volatile bool _active = true;
    private int _deferRender;
    private bool _deferInitialRender;
    private bool _reloadProgressRenderDeferred;

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
        _images = imageRenderer;
        _useAlternateScreen = useAlternateScreen;
        _theme = theme;
        _deferInitialRender = deferInitialRender;
        var renderInterval = minimumRenderInterval ?? TimeSpan.Zero;
        if (renderInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumRenderInterval));
        _render = new(_gate, timeProvider ?? TimeProvider.System, renderInterval,
            () => _active && !_suspended && _deferRender <= 0 && !_deferInitialRender, RenderFrameLocked);
        _terminalColors = new(theme.TerminalForeground, theme.TerminalBackground);
        _getColumns = getColumns ?? ReadColumns;
        _getRows = getRows ?? ReadRows;
        _compositor = new(_originalOut, _images);
        _out = new(this, isError: false);
        _error = new(this, isError: true);
        try
        {
            _originalOut.Write(TerminalKeyboardMode.Enable + (_useAlternateScreen
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
                TerminalKeyboardMode.Disable);
            _originalOut.Flush();
            _active = false;
            throw;
        }
    }

    public TextWriter Output => _out;
    public TextWriter Error => _error;
    public bool IsActive => _active;
    internal int TerminalWidth => Columns();
    internal int TerminalHeight => Rows();
    internal TerminalTheme CurrentTheme { get { lock (_gate) return _theme; } }
    internal TerminalColorState CurrentTerminalColors => _terminalColorQuery?.Current ?? _terminalColors;
    internal bool ToolResultsExpanded { get { lock (_gate) return _transcript.IsExpanded; } }
    internal string? SelectedText
    {
        get { lock (_gate) return _mouse.SelectedText; }
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
                TerminalKeyboardMode.Disable);
            _originalOut.Flush();
            _suspended = true;
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (!_active || !_suspended) return;
            _originalOut.Write(TerminalKeyboardMode.Enable + (_useAlternateScreen
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
            _originalOut.Write(TerminalKeyboardMode.Disable);
            _originalOut.Flush();
            _terminalModeShutdownPrepared = true;
            _render.Request(immediate: true);
            return true;
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
            _images.Clear();
            _lastImagePruneRevision = -1;
            _search.Clear();
            _mouse.Clear();
            _scrollOffset = 0;
            _liveAssistant = "";
            _liveAssistantSource = "";
            _liveThinking = "";
            _liveThinkingSource = "";
            _hasConversationContent = false;
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

    internal void SetAssistantText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        lock (_gate)
        {
            if (!_active) return;
            _liveAssistantSource = TerminalSafeText.Normalize(markdown);
            _liveAssistant = RenderMarkdown(_liveAssistantSource) + "\n";
            _render.Request();
        }
    }

    internal void SetAssistantThinkingText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _liveThinkingSource = TerminalSafeText.Normalize(text);
            _liveThinking = RenderThinking(_theme, _liveThinkingSource, _thinkingBlocksHidden);
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
            _liveThinking = "";
            _liveThinkingSource = "";
            AppendThinkingBlockLocked(safe);
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
            AppendThinkingBlockLocked(safe);
            _render.Request();
        }
    }

    internal void SetThinkingBlocksHidden(bool hidden)
    {
        lock (_gate)
        {
            if (!_active || _thinkingBlocksHidden == hidden) return;
            _thinkingBlocksHidden = hidden;
            _transcript.SetThinkingBlocksHidden(hidden, _theme);
            _liveThinking = _liveThinkingSource.Length == 0 ? "" :
                RenderThinking(_theme, _liveThinkingSource, _thinkingBlocksHidden);
            _render.Request();
        }
    }

    internal bool ThinkingBlocksHidden { get { lock (_gate) return _thinkingBlocksHidden; } }

    private void AppendThinkingBlockLocked(string text)
    {
        string RenderVisible(TerminalTheme theme) => RenderThinking(theme, text, hidden: false);
        string RenderHidden(TerminalTheme theme) => RenderThinking(theme, text, hidden: true);
        _transcript.AppendThinkingBlock(text, "Thinking...", RenderVisible, RenderHidden, _theme,
            _thinkingBlocksHidden);
        _hasConversationContent = true;
    }

    internal void CommitAssistantText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        lock (_gate)
        {
            if (!_active) return;
            var safe = TerminalSafeText.Normalize(markdown);
            var rendered = RenderMarkdown(safe);
            _liveAssistant = "";
            _liveAssistantSource = "";
            if (rendered.Length > 0) _hasConversationContent = true;
            _transcript.AppendMarkdown(safe, rendered, isError: false);
            _render.Request();
        }
    }

    internal void AppendThemedStatusLine(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        string Render(TerminalTheme theme) => Environment.NewLine + " " + theme.Style("dim", safe) + Environment.NewLine;
        lock (_gate)
        {
            if (!_active) return;
            _transcript.AppendThemedStatus(Render(_theme), Render,
                Environment.NewLine + " " + safe + Environment.NewLine);
            _render.Request();
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
        string Render(TerminalTheme theme) => Environment.NewLine + " " + theme.Style("dim", safe) + Environment.NewLine;
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
            _transcript.ReRenderMarkdown(RenderMarkdown);
            _transcript.ReRenderThemedSegments(_theme);
            _liveAssistant = _liveAssistantSource.Length == 0 ? "" : RenderMarkdown(_liveAssistantSource);
            _liveThinking = _liveThinkingSource.Length == 0 ? "" :
                RenderThinking(_theme, _liveThinkingSource, _thinkingBlocksHidden);
            _render.Request();
        }
    }

    internal void SetMarkdownCodeBlockIndent(string codeBlockIndent)
    {
        ArgumentNullException.ThrowIfNull(codeBlockIndent);
        lock (_gate)
        {
            if (string.Equals(_markdownCodeBlockIndent, codeBlockIndent, StringComparison.Ordinal)) return;
            _markdownCodeBlockIndent = codeBlockIndent;
            _transcript.ReRenderMarkdown(RenderMarkdown);
            _liveAssistant = _liveAssistantSource.Length == 0 ? "" : RenderMarkdown(_liveAssistantSource);
            _render.Request();
        }
    }

    internal void SetThemeResolver(Func<TerminalColorState, TerminalTheme> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_gate) _themeResolver = resolver;
    }

    internal void AttachTerminalInput(TerminalInput input) => _terminalInput = input;

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
            _transcript.ReRenderMarkdown(RenderMarkdown);
            _transcript.ReRenderThemedSegments(_theme);
            _liveAssistant = _liveAssistantSource.Length == 0 ? "" : RenderMarkdown(_liveAssistantSource);
            _liveThinking = _liveThinkingSource.Length == 0 ? "" :
                RenderThinking(_theme, _liveThinkingSource, _thinkingBlocksHidden);
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

    internal void SetWorkingStatus(string? status, bool render = true)
    {
        lock (_gate)
        {
            if (!_active) return;
            _workingStatus = string.IsNullOrWhiteSpace(status) ? null : TerminalSafeText.Normalize(status);
            if (render) _render.Request();
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
            var (prefix, isError) = kind switch
            {
                TerminalStatusNotificationKind.Warning => ("Warning: ", false),
                TerminalStatusNotificationKind.Error => ("Error: ", true),
                _ => ("", false)
            };
            _transcript.AppendCaptured(Environment.NewLine + " " + prefix + safe + Environment.NewLine, isError);
            _statusNotification = safe;
            _statusNotificationKind = kind;
            _statusNotificationIsExtension = false;
            _render.Request();
        }
    }

    internal void AppendStatusMessage(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        var prefix = kind switch
        {
            TerminalStatusNotificationKind.Warning => "Warning: ",
            TerminalStatusNotificationKind.Error => "Error: ",
            _ => ""
        };
        lock (_gate)
        {
            if (!_active) return;
            _transcript.AppendCaptured(Environment.NewLine + " " + prefix + safe + Environment.NewLine,
                kind == TerminalStatusNotificationKind.Error);
            _statusNotification = safe;
            _statusNotificationKind = kind;
            _statusNotificationIsExtension = true;
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
            _transcript.AppendCaptured(Environment.NewLine + " " + safe + Environment.NewLine, isError: false);
            _statusNotification = safe;
            _statusNotificationKind = TerminalStatusNotificationKind.Info;
            _statusNotificationIsExtension = true;
            _render.Request();
        }
    }

    internal void DismissEditorPanelAndAppendStatus(string message,
        TerminalStatusNotificationKind kind = TerminalStatusNotificationKind.Info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var safe = TerminalSafeText.Normalize(message);
        var prefix = kind switch
        {
            TerminalStatusNotificationKind.Warning => "Warning: ",
            TerminalStatusNotificationKind.Error => "Error: ",
            _ => ""
        };
        lock (_gate)
        {
            if (!_active) return;
            _editorPanel = null;
            _panelCursorRow = null;
            _panelCursorColumn = null;
            _panelCursorVisible = false;
            _transcript.AppendCaptured(Environment.NewLine + " " + prefix + safe + Environment.NewLine,
                kind == TerminalStatusNotificationKind.Error);
            _statusNotification = safe;
            _statusNotificationKind = kind;
            _statusNotificationIsExtension = true;
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
            if (_lastColumns != 0 && columns != _lastColumns)
            {
                _transcript.ReRenderMarkdown(RenderMarkdown);
                _liveAssistant = _liveAssistantSource.Length == 0 ? "" : RenderMarkdown(_liveAssistantSource);
            }
            if (columns != _lastColumns || rows != _lastRows) _render.Request(immediate: true);
        }
    }

    internal void ScrollPage(bool up)
    {
        lock (_gate)
        {
            if (!_active) return;
            var pageRows = Math.Max(1, _lastRows * 2 / 3 - 2);
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
                var delta = mouse.WheelScrollDelta;
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
            var activeText = new StringBuilder();
            var capturedText = new StringBuilder();
            var skillInvocation = TerminalSkillInvocationParser.Parse(safe);
            if (skillInvocation is not null)
            {
                AppendSkillInvocationLocked(skillInvocation);
                if (skillInvocation.UserMessage is { Length: > 0 } userMessage)
                    AppendSkillUserMessageLocked(userMessage);
            }
            else if (safe.Length > 0) AppendUserTextLocked(safe);
            if (images is not null)
            {
                foreach (var image in images)
                {
                    var marker = _images.Register(image, out var fallback);
                    activeText.Append(marker ?? fallback).Append(Environment.NewLine);
                    capturedText.Append(fallback).Append(Environment.NewLine);
                }
            }
            if (skillInvocation is null && safe.Length == 0 && activeText.Length == 0) return;
            if (activeText.Length > 0)
                _transcript.Append(activeText.ToString(), isError: false, capturedText: capturedText.ToString());
            if (skillInvocation is not null || safe.Length > 0 || images is { Count: > 0 })
                _hasConversationContent = true;
            _render.Request();
        }
    }

    internal void SetToolExpandKeyLabel(string? keyLabel)
    {
        var safe = TerminalSafeText.Normalize(keyLabel ?? string.Empty);
        lock (_gate)
        {
            if (_toolExpandKeyLabel.Equals(safe, StringComparison.Ordinal)) return;
            _toolExpandKeyLabel = safe;
            _transcript.ReRenderThemedSegments(_theme);
            _render.Request();
        }
    }

    private void AppendSkillInvocationLocked(TerminalSkillInvocation skillInvocation)
    {
        string Render(TerminalTheme theme, bool expanded) => TerminalSkillInvocationRenderer.RenderSkill(
            skillInvocation, Columns(), theme, _toolExpandKeyLabel, _markdownCodeBlockIndent, expanded,
            includeLeadingSpacer: _hasConversationContent);
        var keyHint = string.IsNullOrWhiteSpace(_toolExpandKeyLabel)
            ? ""
            : $" ({_toolExpandKeyLabel.ToLowerInvariant()} to expand)";
        var captured = Environment.NewLine + $" [skill] {skillInvocation.Name}{keyHint}" + Environment.NewLine;
        var expanded = Render(_theme, true);
        var collapsed = Render(_theme, false);
        _transcript.AppendThemed(expanded, isError: false, isToolResult: false,
            theme => Render(theme, true), captured, collapsedPreviewText: collapsed,
            collapsedRenderer: theme => Render(theme, false), isCollapsible: true);
    }

    private void AppendSkillUserMessageLocked(string text)
    {
        string Render(TerminalTheme theme) => TerminalSkillInvocationRenderer.RenderUserMessage(
            text, Columns(), theme, _markdownCodeBlockIndent);
        _transcript.AppendThemed(Render(_theme), isError: false, isToolResult: false, Render,
            Environment.NewLine + Environment.NewLine + " " + text + Environment.NewLine);
    }

    private void AppendUserTextLocked(string text)
    {
        var includeLeadingSpacer = _hasConversationContent;
        string Render(TerminalTheme theme) => TerminalSkillInvocationRenderer.RenderUserMessage(
            text, Columns(), theme, _markdownCodeBlockIndent, includeLeadingSpacer);
        _transcript.AppendThemed(Render(_theme), isError: false, isToolResult: false, Render,
            Environment.NewLine + Environment.NewLine + " " + text + Environment.NewLine);
    }

    internal string? ActiveAuthorizationUrl
    {
        get { lock (_gate) return _authorizationUrl; }
    }

    internal void ClearMcpAuthorizationNotification(string serverName)
    {
        ClearAuthorizationUrl("mcp:" + serverName);
    }

    internal void ClearAuthorizationUrl(string owner)
    {
        lock (_gate)
        {
            if (!string.Equals(_authorizationUrlOwner, owner, StringComparison.Ordinal)) return;
            _authorizationUrlOwner = null;
            _authorizationUrl = null;
            _authorizationUrlCopyStatus = null;
        }
    }

    internal bool SetAuthorizationUrlCopyStatus(string message, TerminalStatusNotificationKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        lock (_gate)
        {
            if (!_active || _authorizationUrl is null || _editorPanel is null) return false;
            _authorizationUrlCopyStatus = TerminalSafeText.Normalize(message);
            _authorizationUrlCopyStatusKind = kind;
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
            "Sign in to MCP server \"" + safeName + "\" in your browser:", null, authorizationUrl,
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
        var safeTarget = TerminalSafeText.Normalize(target);
        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        var open = "\u001b]8;;" + safeTarget + "\u001b\\";
        const string close = "\u001b]8;;\u001b\\";
        var keyHints = new List<string>();
        if (!string.IsNullOrWhiteSpace(cancelKeyLabel))
            keyHints.Add(_theme.Style("dim", TerminalSafeText.Normalize(cancelKeyLabel) + " to cancel"));
        if (!string.IsNullOrWhiteSpace(copyKeyLabel))
            keyHints.Add(_theme.Style("dim", TerminalSafeText.Normalize(copyKeyLabel) + " to copy"));
        var notification = TerminalSafeText.Normalize(heading) + "\n" +
            (message is null ? "" : message + "\n") +
            _theme.Style("accent", open + safeTarget + close) + "\n" +
            _theme.Style("dim", open + clickHint + close) +
            (keyHints.Count == 0 ? "" : "\n" + string.Join(" • ", keyHints)) + "\n";
        lock (_gate)
        {
            if (!_active) return false;
            _authorizationUrlOwner = owner;
            _authorizationUrl = target;
            _authorizationUrlCopyStatus = null;
            _transcript.Append(notification, isError: false);
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
            var activeText = new StringBuilder(safe);
            var capturedText = new StringBuilder(safe);
            if (images is not null)
            {
                foreach (var image in images)
                {
                    var marker = _images.Register(image, out var fallback);
                    activeText.Append(Environment.NewLine).Append(marker ?? fallback);
                    capturedText.Append(Environment.NewLine).Append(fallback);
                }
            }
            if (activeText.Length == 0) return;
            var previewText = capturedText.ToString();
            _transcript.Append(activeText.ToString(), isError: true, isToolResult: true,
                previewText, collapsedPreviewText: previewText);
            _render.Request();
        }
    }

    internal void AppendToolCall(PiSharpToolRenderView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        lock (_gate)
        {
            if (!_active) return;
            string Render(TerminalTheme theme) => Environment.NewLine +
                EnsureTrailingNewline(TerminalToolPresentation.Render(view, theme));
            var rendered = Render(_theme);
            var captured = Environment.NewLine + EnsureTrailingNewline(TerminalToolPresentation.Render(view, theme: null));
            _transcript.AppendThemed(rendered, isError: true, isToolResult: false, Render, captured);
            _render.Request();
        }
    }

    internal void AppendToolCall(PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        lock (_gate)
        {
            if (!_active) return;
            string RenderExpanded(TerminalTheme theme) => Environment.NewLine +
                EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, theme));
            string RenderCollapsed(TerminalTheme theme) => Environment.NewLine +
                EnsureTrailingNewline(TerminalToolPresentation.Render(collapsed, theme));
            _transcript.AppendThemed(RenderExpanded(_theme), isError: true, isToolResult: true,
                RenderExpanded, RenderCollapsed(_theme), collapsedPreviewText: RenderCollapsed(_theme),
                collapsedRenderer: RenderCollapsed);
            _render.Request();
        }
    }

    internal void AppendToolResult(PiSharpToolRenderView view, IReadOnlyList<DataContent>? images = null) =>
        AppendToolResult(view, view, images);

    internal void AppendToolResult(PiSharpToolRenderView collapsed, PiSharpToolRenderView expanded,
        IReadOnlyList<DataContent>? images = null)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        lock (_gate)
        {
            if (!_active) return;
            var activeImageText = new StringBuilder();
            var capturedImageText = new StringBuilder();
            if (images is not null)
            {
                foreach (var image in images)
                {
                    var marker = _images.Register(image, out var fallback);
                    activeImageText.Append(Environment.NewLine).Append(marker ?? fallback);
                    capturedImageText.Append(Environment.NewLine).Append(fallback);
                }
            }
            string RenderExpanded(TerminalTheme theme) => EnsureTrailingNewline(
                TerminalToolPresentation.Render(expanded, theme) + activeImageText);
            string RenderCollapsed(TerminalTheme theme) => EnsureTrailingNewline(
                TerminalToolPresentation.Render(collapsed, theme) + capturedImageText);
            var rendered = RenderExpanded(_theme);
            var captured = EnsureTrailingNewline(TerminalToolPresentation.Render(expanded, theme: null) + capturedImageText);
            if (rendered.Length == 0) return;
            _transcript.AppendThemed(rendered, isError: true, isToolResult: true, RenderExpanded, captured,
                collapsedPreviewText: RenderCollapsed(_theme), collapsedRenderer: RenderCollapsed);
            _render.Request();
        }
    }

    private static string EnsureTrailingNewline(string text) =>
        text.Length == 0 || text.EndsWith('\n') ? text : text + Environment.NewLine;

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
        _render.Request();
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
        try
        {
            if (!Console.IsInputRedirected && _terminalColorQuery is { HasPendingReplies: true } query)
            {
                var input = _terminalInput ?? TerminalInput.OpenConsole();
                using var mode = TerminalMode.Enter(this);
                if (_terminalInput is null)
                {
                    input.TerminalColorReceived += query.HandleColorResponse;
                    input.TerminalDeviceAttributesReceived += query.HandleDeviceAttributes;
                }
                query.CompletePendingReplies(input);
            }
        }
        catch (IOException) { }
        finally { _terminalColorQuery?.Dispose(); }
        IReadOnlyList<TerminalTranscriptBuffer.CapturedChunk> captured;
        bool truncated;
        IReadOnlyList<string>? documentRows = null;
        bool terminalModeShutdownPrepared;
        lock (_gate)
        {
            if (!_active) return;
            _render.Flush();
            _render.Dispose();
            _active = false;
            terminalModeShutdownPrepared = _terminalModeShutdownPrepared;
            if (_activated)
            {
                if (ReferenceEquals(Console.Out, _installedOut)) Console.SetOut(_originalOut);
                if (ReferenceEquals(Console.Error, _installedError)) Console.SetError(_originalError);
            }
            _originalOut.Write((_useAlternateScreen ? "\u001b[?2026h" : "") + _images.CleanupControlSequence() + (_useAlternateScreen
                    ? TerminalMouseMode.Disable + "\u001b[?7h"
                    : "") + (_useAlternateScreen ? "\u001b[?2026l" : ""));
            _originalOut.Flush();
            _originalOut.Write("\u001b[?25h" + (terminalModeShutdownPrepared ? "" : TerminalKeyboardMode.Disable));
            _originalOut.Flush();
            if (_useAlternateScreen && _hasConversationContent)
            {
                var columns = Columns();
                var header = _startupHeader?.Invoke(_theme, columns, _startupExpanded) ?? "";
                var startupContent = _startupContent?.Invoke(_theme, columns, _startupExpanded) ?? "";
                var transcript = _images.LayoutTranscript(_transcript.GetTextForScrollback(columns, _theme) + _liveThinking + _liveAssistant,
                    columns, Math.Max(1, Rows() - 5));
                var headerRows = string.IsNullOrEmpty(header)
                    ? Array.Empty<string>()
                    : TerminalTextLayout.Create(header, columns).Rows;
                var contentText = (string.IsNullOrEmpty(startupContent) ? "" : startupContent + "\n") + transcript;
                var contentRows = TerminalTextLayout.Create(contentText, columns).Rows;
                documentRows = headerRows.Concat(contentRows).ToArray();
            }
            if (_useAlternateScreen)
                _compositor.LeaveAlternateScreen(terminalModeShutdownPrepared ? "\u001b[?2004l" : "",
                    restoreDocumentFollows: documentRows is not null);
            else if (terminalModeShutdownPrepared)
                _originalOut.Write("\u001b[?2004l");
            captured = _transcript.CaptureScrollbackSnapshot();
            truncated = _transcript.CaptureTruncated;
        }

        if (documentRows is null)
        {
            foreach (var chunk in captured)
            {
                var writer = chunk.IsError ? _originalError : _originalOut;
                writer.Write(chunk.Text);
                writer.Flush();
            }
        }
        if (truncated)
        {
            _originalError.WriteLine("Interactive transcript exceeded the scrollback restore limit; the active screen was still rendered in full.");
            _originalError.Flush();
        }
        if (documentRows is not null) _compositor.RestoreDocumentToNormalBuffer(documentRows);
        else if (_useAlternateScreen) _compositor.RestoreLastFrameToNormalBuffer();
        _images.Clear();
    }

    private int _lastColumns;
    private int _lastRows;
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

    private string GetTranscriptTextLocked(int width) => _transcript.GetText(width) + _liveThinking + _liveAssistant;

    private string RenderMarkdown(string markdown)
    {
        var width = Math.Max(1, Columns() - 2);
        var rendered = TerminalMarkdownRenderer.Render(markdown, width, _theme, _markdownCodeBlockIndent);
        var output = new StringBuilder("\n");
        var lines = rendered.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) output.Append('\n');
            var padded = " " + lines[index] + " ";
            output.Append(padded).Append(' ', Math.Max(0, Columns() - TerminalTextLayout.Width(padded)));
        }
        return output.ToString();
    }

    private static string RenderThinking(TerminalTheme theme, string text, bool hidden) =>
        Environment.NewLine + " " + theme.Style("thinkingText", hidden ? "Thinking..." : text, italic: true) +
        Environment.NewLine;

    private void RenderFrameLocked()
    {
        var columns = Columns();
        var rows = Rows();
        if (_startupPanelActive)
        {
            var panel = _editorPanel ?? [];
            var frameRows = Enumerable.Repeat("", rows).ToArray();
            for (var index = 0; index < Math.Min(panel.Count, frameRows.Length); index++)
                frameRows[index] = TerminalTranscriptViewport.Clip(panel[index], columns);
            var cursorRow = Math.Clamp(_panelCursorRow ?? 0, 0, Math.Max(0, rows - 1));
            var cursorColumn = Math.Clamp(_panelCursorColumn ?? 1, 1, columns + 1);
            _compositor.Render(new(frameRows, cursorRow, cursorColumn, 0, columns, rows, _panelCursorVisible));
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
            _statusNotification, _statusNotificationKind, _statusNotificationIsExtension,
            _startupHeader?.Invoke(_theme, columns, _startupExpanded),
            _startupContent?.Invoke(_theme, columns, _startupExpanded), _editorSuggestions, _workingStatus,
            _authorizationUrlCopyStatus, _authorizationUrlCopyStatusKind);
        _scrollOffset = frame.ScrollOffset;
        _lastColumns = frame.Columns;
        _lastRows = frame.Height;
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
