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
    private readonly TerminalTranscriptBuffer _transcript = new();
    private readonly TerminalImageRenderer _images;
    private readonly TerminalScreenCompositor _compositor;
    private TerminalTheme _theme;
    private string _markdownCodeBlockIndent = "  ";
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
    private readonly TranscriptSearchController _search = new();
    private int _editorCursor;
    private int? _editorSelectionStart;
    private int? _editorSelectionEnd;
    private int _scrollOffset;
    private int _lastImagePruneRevision = -1;
    private string _footer = "Enter steers · follow-up queues · Escape aborts";
    private IReadOnlyList<string>? _overlay;
    private IReadOnlyList<string>? _editorPanel;
    private int? _panelCursorRow;
    private int? _panelCursorColumn;
    private bool _panelCursorVisible;
    private int _panelBottomMargin = 1;
    private bool _activated;
    private bool _suspended;
    private volatile bool _active = true;
    private int _deferRender;

    public TerminalScreen(TextWriter originalOut, TextWriter originalError,
        Func<int>? getColumns = null, Func<int>? getRows = null)
        : this(originalOut, originalError, getColumns, getRows, new TerminalImageRenderer(), TerminalTheme.Default)
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
        bool queryTerminalColors, bool followTerminalAppearance = false)
    {
        ArgumentNullException.ThrowIfNull(originalOut);
        ArgumentNullException.ThrowIfNull(originalError);
        ArgumentNullException.ThrowIfNull(imageRenderer);
        ArgumentNullException.ThrowIfNull(theme);
        _originalOut = originalOut;
        _originalError = originalError;
        _images = imageRenderer;
        _theme = theme;
        _terminalColors = new(theme.TerminalForeground, theme.TerminalBackground);
        _getColumns = getColumns ?? ReadColumns;
        _getRows = getRows ?? ReadRows;
        _compositor = new(_originalOut, _images);
        _out = new(this, isError: false);
        _error = new(this, isError: true);
        try
        {
            _originalOut.Write("\u001b[?1049h\u001b[?25l" + TerminalMouseMode.Enable);
            lock (_gate) RenderLocked();
            _terminalColorQuery = new(_originalOut, HandleTerminalColorStateChanged, initialColors: _terminalColors);
            _terminalColorQuery.Start(queryTerminalColors, followTerminalAppearance);
        }
        catch
        {
            _originalOut.Write(TerminalMouseMode.Disable + "\u001b[?25h\u001b[?1049l");
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

    internal void Suspend()
    {
        lock (_gate)
        {
            if (!_active || _suspended) return;
            _originalOut.Write(_images.HidePlacements() + TerminalMouseMode.Disable + "\u001b[?25h\u001b[?1049l");
            _originalOut.Flush();
            _suspended = true;
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (!_active || !_suspended) return;
            _originalOut.Write("\u001b[?1049h\u001b[?25l" + TerminalMouseMode.Enable);
            _suspended = false;
            RenderLocked();
        }
    }

    public void SetEditor(string text, int cursor, int? selectionStart = null, int? selectionEnd = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _editorText = text;
            _editorCursor = Math.Clamp(cursor, 0, text.Length);
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
            RenderLocked();
        }
    }

    internal void ReplaceTranscript(Action<TerminalScreen> appendHistory)
    {
        ArgumentNullException.ThrowIfNull(appendHistory);
        lock (_gate)
        {
            if (!_active) return;
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
            _overlay = null;
            _deferRender++;
            try { appendHistory(this); }
            finally
            {
                _deferRender--;
                RenderLocked();
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
            _liveAssistant = RenderMarkdown(_liveAssistantSource);
            RenderLocked();
        }
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
            _transcript.AppendMarkdown(safe, rendered, isError: false);
            RenderLocked();
        }
    }

    internal void SetTheme(TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        lock (_gate)
        {
            if (!_active) return;
            _theme = theme.WithTerminalColors(_terminalColors.Foreground, _terminalColors.Background);
            _transcript.ReRenderMarkdown(RenderMarkdown);
            _transcript.ReRenderToolViews(_theme);
            _liveAssistant = _liveAssistantSource.Length == 0 ? "" : RenderMarkdown(_liveAssistantSource);
            RenderLocked();
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
            RenderLocked();
        }
    }

    internal void SetThemeResolver(Func<TerminalColorState, TerminalTheme> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_gate) _themeResolver = resolver;
    }

    internal void AttachTerminalInput(TerminalInput input) => _terminalInput = input;

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
            _transcript.ReRenderToolViews(_theme);
            _liveAssistant = _liveAssistantSource.Length == 0 ? "" : RenderMarkdown(_liveAssistantSource);
            RenderLocked();
        }
    }

    public void SetFooter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            if (!_active) return;
            _footer = TerminalSafeText.Normalize(text);
            RenderLocked();
        }
    }

    internal void SetOverlay(IReadOnlyList<string>? lines)
    {
        lock (_gate)
        {
            if (!_active) return;
            // List state sanitizes user text before applying application-owned styling.
            _overlay = lines?.Select(line => line.Replace('\n', ' ')).ToArray();
            RenderLocked();
        }
    }

    internal void SetEditorPanel(IReadOnlyList<string>? lines, int? cursorRow = null,
        int? cursorColumn = null, bool cursorVisible = false, int bottomMargin = 1)
    {
        lock (_gate)
        {
            if (!_active) return;
            _editorPanel = lines?.Select(line => line.Replace('\n', ' ')).ToArray();
            _panelCursorRow = cursorRow;
            _panelCursorColumn = cursorColumn;
            _panelCursorVisible = cursorVisible;
            _panelBottomMargin = bottomMargin;
            RenderLocked();
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
            if (columns != _lastColumns || rows != _lastRows) RenderLocked();
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
            RenderLocked();
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
            if (result.Changed) RenderLocked();
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
            RenderLocked();
            return _search.State;
        }
    }

    internal void ClearTranscriptSearch()
    {
        lock (_gate)
        {
            if (!_active || _search.Query.Length == 0) return;
            _search.Clear();
            RenderLocked();
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
            if (safe.Length > 0)
            {
                foreach (var line in safe.Split('\n'))
                {
                    activeText.Append("› ").Append(line).Append(Environment.NewLine);
                    capturedText.Append("› ").Append(line).Append(Environment.NewLine);
                }
            }
            if (images is not null)
            {
                foreach (var image in images)
                {
                    var marker = _images.Register(image, out var fallback);
                    activeText.Append(marker ?? fallback).Append(Environment.NewLine);
                    capturedText.Append(fallback).Append(Environment.NewLine);
                }
            }
            if (activeText.Length == 0) return;
            _transcript.Append(activeText.ToString(), isError: false, capturedText: capturedText.ToString());
            RenderLocked();
        }
    }

    internal void AppendMcpAuthorizationNotification(string serverName, Uri authorizationUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(authorizationUrl);
        if (!authorizationUrl.IsAbsoluteUri ||
            !(authorizationUrl.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              authorizationUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrEmpty(authorizationUrl.Host))
            throw new ArgumentException("MCP authorization URL must use HTTP or HTTPS.", nameof(authorizationUrl));

        var target = authorizationUrl.AbsoluteUri;
        var safeName = TerminalSafeText.Normalize(serverName);
        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        var open = "\u001b]8;;" + target + "\u001b\\";
        const string close = "\u001b]8;;\u001b\\";
        var notification = "Sign in to MCP server \"" + safeName + "\" in your browser:\n" +
            open + target + close + "\n" + open + clickHint + close + "\n";
        lock (_gate)
        {
            if (!_active) return;
            _transcript.Append(notification, isError: false);
            RenderLocked();
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
            RenderLocked();
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
            RenderLocked();
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
            RenderLocked();
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
            RenderLocked();
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
            RenderLocked();
            return expanded;
        }
    }

    internal void SetToolResultsExpanded(bool expanded)
    {
        lock (_gate)
        {
            if (!_active || _transcript.IsExpanded == expanded) return;
            _transcript.SetExpanded(expanded);
            RenderLocked();
        }
    }

    private void SetScrollOffsetLocked(int value)
    {
        _scrollOffset = Math.Clamp(value, 0, MaxTranscriptScrollOffset);
        RenderLocked();
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
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            if (_activated)
            {
                if (ReferenceEquals(Console.Out, _installedOut)) Console.SetOut(_originalOut);
                if (ReferenceEquals(Console.Error, _installedError)) Console.SetError(_originalError);
            }
            _originalOut.Write(_images.CleanupControlSequence() + TerminalMouseMode.Disable + "\u001b[?25h\u001b[?1049l");
            _originalOut.Flush();
            captured = _transcript.CaptureSnapshot();
            truncated = _transcript.CaptureTruncated;
        }

        foreach (var chunk in captured)
        {
            var writer = chunk.IsError ? _originalError : _originalOut;
            writer.Write(chunk.Text);
            writer.Flush();
        }
        _images.Clear();
        if (truncated)
        {
            _originalError.WriteLine("Interactive transcript exceeded the scrollback restore limit; the active screen was still rendered in full.");
            _originalError.Flush();
        }
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
            RenderLocked();
        }
    }

    private string GetTranscriptTextLocked(int width) => _transcript.GetText(width);

    private string RenderMarkdown(string markdown) =>
        TerminalMarkdownRenderer.Render(markdown, Math.Max(1, Columns() - 1), _theme, _markdownCodeBlockIndent);

    private void RenderLocked()
    {
        if (!_active || _suspended || _deferRender > 0) return;
        var columns = Columns();
        var rows = Rows();
        var editorHeight = Math.Clamp(rows / 3, 1, Math.Max(1, rows - 2));
        var footerHeight = rows > 2 ? 1 : 0;
        var transcriptHeight = Math.Max(1, rows - editorHeight - footerHeight);
        if (_lastImagePruneRevision != _transcript.Revision)
        {
            _images.PruneUnreferenced(_transcript.GetRetainedText());
            _lastImagePruneRevision = _transcript.Revision;
        }
        var transcriptWidth = Math.Max(1, columns - 1);
        var transcript = _images.LayoutTranscript(GetTranscriptTextLocked(transcriptWidth) + _liveAssistant,
            transcriptWidth, Math.Max(1, transcriptHeight - 2));
        var frame = _compositor.Compose(_editorText, _editorCursor, _editorSelectionStart, _editorSelectionEnd,
            transcript, _footer, _overlay, _scrollOffset, columns, rows, _search, _mouse, _theme,
            _editorPanel, _panelCursorRow, _panelCursorColumn, _panelCursorVisible, _panelBottomMargin);
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

    private static int ReadColumns()
    {
        try { return Console.WindowWidth; }
        catch (IOException) { return 80; }
    }

    private static int ReadRows()
    {
        try { return Console.WindowHeight; }
        catch (IOException) { return 24; }
    }

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
