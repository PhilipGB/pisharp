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

    [Fact]
    public void BashCommandFittingOneRowLeavesFinalPaddingUnstyled()
    {
        using var session = BashSession.Create(columns: 80);

        session.Finish("echo objective-short", ["objective-short"]);

        AssertCommandRows(session, "echo objective-short", "toolSuccessBg");
        Assert.Contains("objective-short", session.Frame());
    }

    [Fact]
    public void WrappedBashCommandStylesTheSeventeenTrailingPaddingCells()
    {
        const string command =
            "x=objective-line; printf '%s-one\\n' \"$x\"; sleep 0.3; printf '%s-two\\n%s-three\\n' \"$x\" \"$x\"";
        using var session = BashSession.Create(columns: 80);

        session.Finish(command, ["objective-line-one", "objective-line-two", "objective-line-three"]);

        var (frame, _) = AssertCommandRows(session, command, "toolSuccessBg");
        var expected = ExpectedCommandRows(session.Theme, command, session.Columns);
        Assert.Equal(2, expected.Count);
        var first = expected[0];
        var row = frame.Rows[SingleCommandRow(frame, first)];
        var end = row.Text.IndexOf(first, StringComparison.Ordinal) + first.Length;
        Assert.Equal(17, session.Columns - end);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void WrappedBashCommandPaddingFollowsTheTerminalWidth(int columns)
    {
        const string command =
            "printf 'objective-width-%s-%s-%s-%s-%s-%s-%s-%s\\n' one two three four five six seven eight";
        using var session = BashSession.Create(columns);

        session.Finish(command, ["objective-width"]);

        AssertCommandRows(session, command, "toolSuccessBg");
    }

    [Fact]
    public void BashCommandEndingAtTheContentBoundaryOnlyLeavesOutputPaddingUnstyled()
    {
        var command = new string('a', 76);
        using var session = BashSession.Create(columns: 80);

        session.Finish(command, ["boundary-ok"]);

        var (frame, _) = AssertCommandRows(session, command, "toolSuccessBg");
        var row = frame.Rows[SingleCommandRow(frame, "$ " + command)];
        Assert.Equal(" " + "$ " + command + " ", row.Text);
        Assert.True(row.Cells[^1].Style.Background == ThemeColor(session.Theme, "toolSuccessBg"),
            "The output-padding cell must retain the tool background.");
        Assert.Null(row.Cells[^1].Style.Foreground);
    }

    [Fact]
    public void LongBashCommandFollowedByOutputKeepsTheOutputRowPosition()
    {
        const string command =
            "x=objective-line; printf '%s-one\\n' \"$x\"; sleep 0.3; printf '%s-two\\n%s-three\\n' \"$x\" \"$x\"";
        using var session = BashSession.Create(columns: 80);

        session.Finish(command, ["objective-output-marker"]);

        var (frame, lastCommandRow) = AssertCommandRows(session, command, "toolSuccessBg");
        Assert.Equal("", frame.Rows[lastCommandRow + 1].Text.Trim());
        Assert.Contains("objective-output-marker", frame.Rows[lastCommandRow + 2].Text);
    }

    [Fact]
    public void FailedBashCommandThenSuccessfulRecoveryStylesBothWrappedCommands()
    {
        const string failed =
            "printf 'objective-failed-%s-%s-%s-%s-%s-%s-%s-%s\\n' one two three four five six seven eight; exit 1";
        const string recovered =
            "printf 'objective-recovered-%s-%s-%s-%s-%s-%s-%s-%s\\n' one two three four five six seven eight";
        using var session = BashSession.Create(columns: 80);

        session.Finish(failed, ["Failed: 1, Passed: 0, Total: 1"], isError: true);
        var (_, lastFailedRow) = AssertCommandRows(session, failed, "toolErrorBg");
        session.Finish(recovered, ["Passed: 1, Failed: 0, Total: 1"]);
        AssertCommandRows(session, recovered, "toolSuccessBg", searchFrom: lastFailedRow + 1);

        var frame = session.Frame();
        Assert.Contains(FailedMarker, frame);
        Assert.Contains("Passed: 1, Failed: 0, Total: 1", frame);
    }

    [Fact]
    public void ResizeRecomputesWrappedBashCommandStyling()
    {
        const string command =
            "printf 'objective-resize-%s-%s-%s-%s-%s-%s-%s-%s\\n' one two three four five six seven eight";
        using var session = BashSession.Create(columns: 120);

        session.Finish(command, ["objective-resize"]);
        Assert.Single(ExpectedCommandRows(session.Theme, command, 120));
        AssertCommandRows(session, command, "toolSuccessBg");

        session.Resize(columns: 80);
        Assert.Equal(2, ExpectedCommandRows(session.Theme, command, 80).Count);
        AssertCommandRows(session, command, "toolSuccessBg");

        session.Resize(columns: 120);
        Assert.Single(ExpectedCommandRows(session.Theme, command, 120));
        AssertCommandRows(session, command, "toolSuccessBg");
    }

    [Fact]
    public void ExpandedAndRecollapsedBashResultsRetainWrappedCommandStyling()
    {
        const string command =
            "printf 'objective-expanded-%s-%s-%s-%s-%s-%s-%s-%s\\n' one two three four five six seven eight";
        using var session = BashSession.Create(columns: 80);
        var output = Enumerable.Range(1, 8).Select(index => $"objective-expanded-{index}").ToArray();

        session.Finish(command, output);
        Assert.Contains("earlier lines", session.Frame());
        AssertCommandRows(session, command, "toolSuccessBg");

        session.Screen.ToggleToolResultsExpanded();
        var expanded = session.Frame();
        Assert.DoesNotContain("earlier lines", expanded);
        foreach (var line in output) Assert.Contains(line, expanded);
        AssertCommandRows(session, command, "toolSuccessBg");

        session.Screen.ToggleToolResultsExpanded();
        Assert.Contains("earlier lines", session.Frame());
        AssertCommandRows(session, command, "toolSuccessBg");
    }

    private static (TerminalStyledFrameReader.Frame Frame, int LastCommandRow) AssertCommandRows(
        BashSession session, string command, string backgroundToken, int searchFrom = 0)
    {
        var frame = session.StyledFrame();
        var expected = ExpectedCommandRows(session.Theme, command, session.Columns);
        Assert.NotEmpty(expected);
        var title = ThemeColor(session.Theme, "toolTitle");
        var background = ThemeColor(session.Theme, backgroundToken);
        var lastRow = -1;
        for (var index = 0; index < expected.Count; index++)
        {
            var expectedText = expected[index];
            var rowIndex = FindCommandRow(frame, expectedText, searchFrom);
            Assert.True(rowIndex >= 0,
                $"Missing command row {index} \"{expectedText}\" for background {backgroundToken}.");
            var row = frame.Rows[rowIndex];
            var start = row.Text.IndexOf(expectedText, StringComparison.Ordinal);
            Assert.True(start > 0, "The command must start after one output-padding cell.");
            var end = start + expectedText.Length;
            for (var column = start; column < end; column++)
            {
                var cell = row.Cells[column];
                Assert.Equal(expectedText[column - start].ToString(), cell.Text);
                Assert.True(cell.Style.Foreground == title,
                    $"Command cell {column} of \"{expectedText}\" has foreground {cell.Style.Foreground}, expected {title}.");
                Assert.True(cell.Style.Background == background,
                    $"Command cell {column} of \"{expectedText}\" has background {cell.Style.Background}, expected {background}.");
                Assert.True(cell.Style.Bold,
                    $"Command cell {column} of \"{expectedText}\" must stay bold.");
            }

            var continuation = index + 1 < expected.Count;
            for (var column = end; column < session.Columns; column++)
            {
                var cell = row.Cells[column];
                Assert.True(cell.Style.Background == background,
                    $"Padding cell {column} of \"{expectedText}\" has background {cell.Style.Background}, expected {background}.");
                if (continuation)
                {
                    Assert.True(cell.Style.Foreground == title,
                        $"Continuation padding cell {column} of \"{expectedText}\" has foreground {cell.Style.Foreground}, expected {title}.");
                    Assert.True(cell.Style.Bold,
                        $"Continuation padding cell {column} of \"{expectedText}\" must keep the command style.");
                }
                else
                {
                    Assert.Null(cell.Style.Foreground);
                    Assert.False(cell.Style.Bold,
                        $"Final command row cell {column} of \"{expectedText}\" must not keep the command style.");
                }
            }

            searchFrom = rowIndex + 1;
            lastRow = rowIndex;
        }
        return (frame, lastRow);
    }

    private static TerminalStyledFrameReader.Rgb ThemeColor(TerminalTheme theme, string token)
    {
        var color = theme.GetConcreteColor(token);
        return new(color.R, color.G, color.B);
    }

    private static IReadOnlyList<string> ExpectedCommandRows(TerminalTheme theme, string command, int columns)
    {
        var styled = theme.Style("toolTitle", "$ " + command, bold: true);
        return TerminalTextLayout.Wrap(styled, columns - 2)
            .Select(TerminalTextLayout.StripFormatting)
            .ToArray();
    }

    private static int FindCommandRow(TerminalStyledFrameReader.Frame frame, string text, int searchFrom)
    {
        for (var row = searchFrom; row < frame.Rows.Count; row++)
            if (frame.Rows[row].Text.Contains(text, StringComparison.Ordinal)) return row;
        return -1;
    }

    private static int SingleCommandRow(TerminalStyledFrameReader.Frame frame, string text)
    {
        var row = FindCommandRow(frame, text, 0);
        Assert.True(row >= 0, $"Missing command row \"{text}\".");
        return row;
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

        private BashSession(StringWriter output, StringWriter error, int columns, int rows, TerminalTheme theme)
        {
            _output = output;
            _error = error;
            _columns = columns;
            _rows = rows;
            Theme = theme;
            Screen = null!;
            Transcript = null!;
        }

        public TerminalScreen Screen { get; private set; }
        public InteractiveTranscript Transcript { get; private set; }
        public TerminalTheme Theme { get; }
        public int Columns => _columns;
        public int Rows => _rows;

        public static BashSession Create(int columns, int rows = 40, TerminalTheme? theme = null)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var session = new BashSession(output, error, columns, rows,
                theme ?? TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor));
            var screen = new TerminalScreen(output, error, () => session._columns, () => rows,
                new TerminalImageRenderer(), session.Theme,
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

        public TerminalStyledFrameReader.Frame StyledFrame() =>
            TerminalStyledFrameReader.Read(_output.ToString(), _rows, _columns)[^1];

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
