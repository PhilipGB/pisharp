using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalScreenTests
{
    [Fact]
    public void TranscriptAndDraftShareAUnicodeAwareScreenAndRestoreSeparateStreams()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 32, () => 9);
        screen.SetEditor("draft 界", 7);
        screen.Output.Write("answer \u001b[2J🙂\nnext line");
        screen.Error.Write("tool status");
        screen.SetFooter("running");
        screen.Dispose();

        Assert.Contains("draft 界", output.ToString());
        Assert.Contains("answer [2J🙂", output.ToString());
        Assert.Contains("tool status", output.ToString());
        Assert.Contains("answer [2J🙂\nnext line", output.ToString());
        Assert.Contains("tool status", error.ToString());
        Assert.Contains("running", output.ToString());
    }

    [Fact]
    public void TerminalResizeRedrawsTheActiveViewport()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var columns = 40;
        var rows = 8;
        var screen = new TerminalScreen(output, error, () => columns, () => rows);
        screen.SetEditor("long draft text that wraps across the input viewport", 17);
        var previousLength = output.GetStringBuilder().Length;

        columns = 24;
        rows = 12;
        screen.RefreshIfResized();
        screen.Dispose();

        Assert.True(output.GetStringBuilder().Length > previousLength);
        Assert.Contains("long draft text", output.ToString());
    }

    [Fact]
    public void DisposeRestoresConsoleWritersAfterAnActiveRun()
    {
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            var screen = new TerminalScreen(output, error, () => 32, () => 9);
            screen.Activate();
            Console.Write("captured output");
            Console.Error.Write("captured error");
            screen.Dispose();
            Console.Write("after output");
            Console.Error.Write("after error");
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        Assert.Contains("captured output", output.ToString());
        Assert.Contains("captured error", error.ToString());
        Assert.Contains("after output", output.ToString());
        Assert.Contains("after error", error.ToString());
    }

    [Fact]
    public async Task ActiveEditorScrollsTheTranscriptAndReturnsToLiveOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 40, () => 9);
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        for (var index = 0; index < 30; index++) screen.Output.WriteLine($"message {index:D2}");

        var topStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var topFrame = output.ToString()[topStart..];

        var bottomStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var bottomFrame = output.ToString()[bottomStart..];
        screen.Dispose();

        Assert.Contains("message 00", topFrame);
        Assert.DoesNotContain("message 29", topFrame);
        Assert.Contains("message 29", bottomFrame);
        Assert.DoesNotContain("message 00", bottomFrame);
    }

    [Fact]
    public async Task ActiveEditorSearchHighlightsMatchesAndRestoresItsDraftOnEscape()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 100, () => 12);
        screen.Output.Write("needle first result\nother output\nneedle second result\n");
        var editor = new TerminalEditor();
        editor.Prefill("draft to keep");
        editor.AttachScreen(screen);
        var aborted = false;
        Task<bool> Queue(string text, bool followUp, CancellationToken token) => Task.FromResult(true);
        IReadOnlyList<string> ClearQueue() => [];
        void Abort() => aborted = true;

        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.F, shift: true, alt: false, control: true), null), Queue, ClearQueue, Abort);
        editor.AttachScreen(screen);
        var searchStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(new(null, "needle"), Queue, ClearQueue, Abort);
        editor.AttachScreen(screen);
        var searchFrame = output.ToString()[searchStart..];
        var nextStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(new(new ConsoleKeyInfo('\n', ConsoleKey.Enter, false, false, false), null), Queue, ClearQueue, Abort);
        editor.AttachScreen(screen);
        var nextFrame = output.ToString()[nextStart..];
        var closeStart = output.GetStringBuilder().Length;
        await editor.HandleActiveInputAsync(new(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false), null), Queue, ClearQueue, Abort);
        editor.AttachScreen(screen);
        var closeFrame = output.ToString()[closeStart..];
        screen.Dispose();

        Assert.Contains("1/2", searchFrame);
        Assert.Contains("\u001b[7;1mneedle", searchFrame);
        Assert.Contains("2/2", nextFrame);
        Assert.Contains("\u001b[7;1mneedle", nextFrame);
        Assert.DoesNotContain("\u001b[7m", closeFrame);
        Assert.Equal("draft to keep", editor.Draft);
        Assert.False(aborted);
    }

    [Fact]
    public async Task ActiveToolResultsCollapseAfterTenLinesAndExpandWithCtrlO()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new TerminalScreen(output, error, () => 100, () => 30);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        var result = string.Join('\n', Enumerable.Range(1, 12).Select(line => $"line {line}"));
        transcript.Render(new("tool_execution_finished", Tool: "grep", Text: result));
        var collapsedFrame = output.ToString();
        var editor = new TerminalEditor();
        editor.AttachScreen(screen);
        var expandedFrameStart = output.GetStringBuilder().Length;

        await editor.HandleActiveInputAsync(
            new(new ConsoleKeyInfo('\0', ConsoleKey.O, shift: false, alt: false, control: true), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        var expandedFrame = output.ToString()[expandedFrameStart..];
        screen.Dispose();

        Assert.Contains("line 1", collapsedFrame);
        Assert.Contains("line 10", collapsedFrame);
        Assert.Contains("2 more lines; tool output collapsed", collapsedFrame);
        Assert.DoesNotContain("line 11", collapsedFrame);
        Assert.Contains("line 11", expandedFrame);
        Assert.Contains("line 12", expandedFrame);
        Assert.Contains("line 12", error.ToString());
    }
}
