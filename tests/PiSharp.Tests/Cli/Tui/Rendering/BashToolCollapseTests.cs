using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

/// <summary>
/// Pi renders a bash tool result as its last five visual rows plus a muted
/// "... (N earlier lines," hint whose shortcut uses the dim key style. These
/// regressions pin that presentation at the cell level.
/// </summary>
[Collection("Console writers")]
public sealed class BashToolCollapseTests
{
    private const string FailedMarker = "Command exited with code 1";

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    public void BashOutputWithinPreviewLimitIsNotCollapsed(int lineCount)
    {
        var lines = Enumerable.Range(1, lineCount).Select(index => $"payload {index}").ToArray();

        var frame = RenderBash("emit", lines, columns: 120);

        Assert.DoesNotContain("earlier lines", frame);
        foreach (var line in lines) Assert.Contains(line, frame);
    }

    [Fact]
    public void BashOutputAbovePreviewLimitShowsHintAndLastFiveVisualRows()
    {
        var lines = Enumerable.Range(1, 6).Select(index => $"payload {index}").ToArray();

        var frame = RenderBash("emit", lines, columns: 120);
        var rows = Rows(frame);

        Assert.Contains("... (1 earlier lines, ctrl+o to expand)", frame);
        Assert.DoesNotContain("payload 1", frame);
        Assert.Equal("... (1 earlier lines, ctrl+o to expand)", Row(rows, "earlier lines"));
        Assert.True(RowIndex(rows, "payload 2") > RowIndex(rows, "earlier lines"));
    }

    [Fact]
    public void FailedBashOutputCollapsesLikePiWithItsOwnHintRow()
    {
        var output = new[] { "one", "two", "three", "Failed: 1, Passed: 0, Total: 1" };

        var frame = RenderBash("run-tests", output, columns: 120, isError: true);
        var rows = Rows(frame);
        var hintIndex = RowIndex(rows, "earlier lines");

        Assert.Equal("... (2 earlier lines, ctrl+o to expand)", rows[hintIndex].Trim());
        Assert.Equal("three", rows[hintIndex + 1].Trim());
        Assert.Equal("Failed: 1, Passed: 0, Total: 1", rows[hintIndex + 2].Trim());
        Assert.Equal("", rows[hintIndex + 3].Trim());
        Assert.Equal("", rows[hintIndex + 4].Trim());
        Assert.Equal(FailedMarker, rows[hintIndex + 5].Trim());
        Assert.Equal("Took 0.3s", rows[hintIndex + 7].Trim());
        Assert.DoesNotContain("one", frame);
        Assert.DoesNotContain("two", frame);
    }

    [Fact]
    public void CollapseCountsVisualRowsAcrossWrappedLines()
    {
        var longLine = new string('x', 500);

        var wide = RenderBash("emit", [longLine], columns: 120);
        var narrow = RenderBash("emit", [longLine], columns: 80);

        Assert.DoesNotContain("earlier lines", wide);
        Assert.Contains("... (2 earlier lines, ctrl+o to expand)", narrow);
        Assert.Equal(5, Rows(narrow).Count(row => row.TrimStart().StartsWith("xxx", StringComparison.Ordinal)));
    }

    [Fact]
    public void FailedAndRecoveredBashBlocksKeepPiRowSpacing()
    {
        var frame = RenderBashSequence(
            ("run-tests", new[] { "one", "two", "three", "Failed: 1, Passed: 0, Total: 1" }, true),
            ("recovery", new[] { "Passed: 1, Failed: 0, Total: 1" }, false));
        var rows = Rows(frame);

        var firstCommand = RowIndex(rows, "run-tests");
        var secondCommand = RowIndex(rows, "recovery");
        Assert.True(secondCommand > firstCommand);
        Assert.Equal(13, secondCommand - firstCommand);
        Assert.Contains("Passed: 1, Failed: 0, Total: 1", frame);
        Assert.DoesNotContain("... (0 earlier lines", frame);
    }

    [Fact]
    public void CtrlOTogglesFullOutputAndRestoresTheCollapsedPreview()
    {
        var lines = Enumerable.Range(1, 8).Select(index => $"payload {index}").ToArray();
        using var session = BashSession.Create(columns: 120);

        session.Finish("emit", lines);

        var collapsed = session.Frame();
        Assert.Contains("... (3 earlier lines, ctrl+o to expand)", collapsed);
        Assert.DoesNotContain("payload 1", collapsed);

        session.Screen.ToggleToolResultsExpanded();
        var expanded = session.Frame();
        Assert.DoesNotContain("earlier lines", expanded);
        foreach (var line in lines) Assert.Contains(line, expanded);

        session.Screen.ToggleToolResultsExpanded();
        Assert.Equal(collapsed, session.Frame());
    }

    [Fact]
    public void CollapsedHintUsesMutedTextAndDimShortcut()
    {
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        using var session = BashSession.Create(columns: 120, theme: theme);

        session.Finish("emit", ["a", "b", "c", "d", "e", "f"]);
        var raw = session.RawOutput();

        Assert.Contains(theme.Style("muted", "... (1 earlier lines,"), raw);
        Assert.Contains(theme.Style("dim", "ctrl+o"), raw);
        Assert.DoesNotContain(theme.Style("muted", "... (1 earlier lines, "), raw);
    }

    [Fact]
    public void ResizingRecomputesTheCollapsedPreviewForTheNewWidth()
    {
        var longLine = new string('x', 500);
        using var session = BashSession.Create(columns: 120);

        session.Finish("emit", [longLine]);
        Assert.DoesNotContain("earlier lines", session.Frame());

        session.Resize(columns: 80);
        Assert.Contains("... (2 earlier lines, ctrl+o to expand)", session.Frame());

        session.Resize(columns: 120);
        Assert.DoesNotContain("earlier lines", session.Frame());
    }

    [Fact]
    public void RunningBashNeverShowsCompletedOutputOrTimer()
    {
        using var session = BashSession.Create(columns: 120);

        session.Start("emit");
        session.Update("partial line\n");

        var running = session.Frame();
        Assert.Contains("partial line", running);
        Assert.Contains("Elapsed 0.0s", running);
        Assert.DoesNotContain("Took", running);

        session.Screen.ToggleToolResultsExpanded();
        var expandedWhileRunning = session.Frame();
        Assert.Contains("partial line", expandedWhileRunning);
        Assert.Contains("Elapsed 0.0s", expandedWhileRunning);
    }

    [Fact]
    public void CompletedAndFailedBashBlocksDoNotRetainTransientActivity()
    {
        using var session = BashSession.Create(columns: 120);

        session.Start("run-tests");
        session.Update("one\ntwo\n");
        session.Finish("run-tests", ["one", "two", "three", FailedMarker], isError: true);

        var frame = session.Frame();
        Assert.Contains(FailedMarker, frame);
        Assert.Contains("Took 0.3s", frame);
        Assert.DoesNotContain("Elapsed", frame);
    }

    private static string RenderBash(string command, IReadOnlyList<string> lines, int columns, bool isError = false)
    {
        using var session = BashSession.Create(columns);
        session.Finish(command, lines, isError);
        return session.Frame();
    }

    private static string RenderBashSequence(
        (string Command, IReadOnlyList<string> Lines, bool IsError) first,
        (string Command, IReadOnlyList<string> Lines, bool IsError) second)
    {
        using var session = BashSession.Create(columns: 120);
        session.Finish(first.Command, first.Lines, first.IsError);
        session.Finish(second.Command, second.Lines, second.IsError);
        return session.Frame();
    }

    private static string[] Rows(string frame) => frame.Split('\n');

    private static int RowIndex(string[] rows, string value) =>
        Array.FindIndex(rows, row => row.Contains(value, StringComparison.Ordinal));

    private static string Row(string[] rows, string value) => rows[RowIndex(rows, value)].Trim();

    private sealed class BashSession : IDisposable
    {
        private readonly StringWriter _output;
        private readonly StringWriter _error;
        private readonly int _rows;
        private int _columns;
        private int _sequence;
        private string? _activeId;

        private BashSession(StringWriter output, StringWriter error, int columns, int rows)
        {
            _output = output;
            _error = error;
            _columns = columns;
            _rows = rows;
            Screen = null!;
            Transcript = null!;
        }

        public TerminalScreen Screen { get; private set; }
        public InteractiveTranscript Transcript { get; private set; }

        public static BashSession Create(int columns, int rows = 40, TerminalTheme? theme = null)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var session = new BashSession(output, error, columns, rows);
            var screen = new TerminalScreen(output, error, () => session._columns, () => rows,
                new TerminalImageRenderer(),
                theme ?? TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor),
                queryTerminalColors: false, timeProvider: new ManualTimeProvider());
            session.Screen = screen;
            session.Transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
            return session;
        }

        public void Start(string command)
        {
            var id = $"bash-{++_sequence}";
            _activeId = id;
            Transcript.Render(new("tool_execution_started", Tool: "bash", OperationId: id)
            {
                ToolArguments = new Dictionary<string, object?> { ["command"] = command }
            });
        }

        public void Update(string text) =>
            Transcript.Render(new("tool_execution_update", Tool: "bash", OperationId: _activeId, Text: text));

        public void Finish(string command, IReadOnlyList<string> lines, bool isError = false)
        {
            if (_activeId is null) Start(command);
            var id = _activeId!;
            // Pi appends the exit status to the untrimmed stdout, so a trailing
            // newline in the command output leaves two blank lines before it.
            var text = string.Join('\n', lines) + "\n";
            if (isError) text += "\n\n" + FailedMarker;
            Transcript.Render(new("tool_execution_finished", Tool: "bash", OperationId: id,
                Text: text, IsError: isError)
            {
                DurationMs = 300
            });
            _activeId = null;
        }

        public string Frame() =>
            TerminalOutputFrameReader.Read(_output.ToString(), _rows, _columns)[^1].Screen;

        public void Resize(int columns)
        {
            _columns = columns;
            Screen.RefreshIfResized();
        }

        public string RawOutput() => _output.ToString();

        public void Dispose()
        {
            Screen.Dispose();
            _output.Dispose();
            _error.Dispose();
        }
    }
}
