using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CodemodeTimerTests
{
    [Fact]
    public void ConcurrentRowsHaveIndependentClocksAndOnlyLiveRowsRedraw()
    {
        var clock = new ManualTimeProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 20,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false, timeProvider: clock);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        void Start(string id)
        {
            transcript.Render(new("tool_execution_started", Tool: "codemode", OperationId: id));
            transcript.Render(new("tool_execution_update", Text: "", Tool: "codemode", OperationId: id));
        }
        Start("first");
        clock.Advance(TimeSpan.FromSeconds(2));
        Start("second");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains("Elapsed 3.0s", Frame(output));
        Assert.Contains("Elapsed 1.0s", Frame(output));
        transcript.Render(new("tool_execution_finished", Text: "first done", Tool: "codemode", OperationId: "first")
        { DurationMs = 4500 });
        Assert.Equal(1, clock.ActiveTimerCount);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains("Took 4.5s", Frame(output));
        Assert.Contains("Elapsed 2.0s", Frame(output));
        transcript.Render(new("tool_execution_finished", Text: "second failed", Tool: "codemode", OperationId: "second", IsError: true));
        Assert.Contains("Took 2.0s", Frame(output));
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public void DuplicateProgressAndPartialErrorsDoNotRestartTheClock()
    {
        var clock = new ManualTimeProvider();
        var redraws = 0;
        using var timers = new TerminalToolTimers(clock, () => redraws++);
        timers.Start("script");
        Assert.Equal("Elapsed 0.0s", timers.Progress("script", isError: false));
        clock.Advance(TimeSpan.FromSeconds(3));
        timers.Start("script");
        Assert.Equal("Elapsed 3.0s", timers.Progress("script", isError: false));
        Assert.Equal(1, clock.ActiveTimerCount);
        Assert.Equal("Elapsed 3.0s", timers.Progress("script", isError: true));
        Assert.Equal(0, clock.ActiveTimerCount);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("Elapsed 3.0s", timers.Progress("script", isError: false));
        Assert.Equal(3, redraws);
        Assert.Equal(5100, timers.Complete("script", 5100));
        Assert.Empty(timers.Rows());
    }

    [Fact]
    public void CollapsedLongOutputStillShowsTheRecordedTotal()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 20);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        transcript.Render(new("tool_execution_started", Tool: "codemode", OperationId: "script"));
        transcript.Render(new("tool_execution_update", Text: "", Tool: "codemode", OperationId: "script"));
        transcript.Render(new("tool_execution_finished", Text: new string('x', 2000), Tool: "codemode", OperationId: "script")
        { DurationMs = 5100 });
        Assert.Contains("Took 5.1s", Frame(output));
        Assert.DoesNotContain("Elapsed", Frame(output));
    }

    [Theory]
    [InlineData("turn_interrupted")]
    [InlineData("turn_failed")]
    [InlineData("finish")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public void AbandonedRowsStopTheirTimersAndLeaveNoLiveElapsedLine(string how)
    {
        var clock = new ManualTimeProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 20,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false, timeProvider: clock);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        transcript.Render(new("tool_execution_started", Tool: "codemode", OperationId: "script"));
        transcript.Render(new("tool_execution_update", Text: "", Tool: "codemode", OperationId: "script"));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Contains("Elapsed 3.0s", Frame(output));
        if (how == "finish") transcript.FinishTurn();
        else if (how == "replace") transcript.LoadHistory(new ConversationSession(Path.GetTempPath(), "fixture", null));
        else if (how == "dispose") screen.Dispose();
        else transcript.Render(new(how));
        Assert.Equal(0, clock.ActiveTimerCount);
        if (how != "dispose") Assert.DoesNotContain("Elapsed", Frame(output));
        var length = output.GetStringBuilder().Length;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(length, output.GetStringBuilder().Length);
    }

    [Fact]
    public void UpdatingAToolCapturePreservesInterveningTranscriptText()
    {
        var buffer = new TerminalTranscriptBuffer();
        static string Render(TerminalTheme _) => "Elapsed 0.0s\n";
        buffer.SetToolResult("script", "Elapsed 0.0s\n", "Elapsed 0.0s\n", Render, Render,
            "Elapsed 0.0s\n", isError: false, finished: false);
        buffer.Append("intervening content\n", isError: false);
        static string Final(TerminalTheme _) => "Took 5.1s\n";
        buffer.SetToolResult("script", "Took 5.1s\n", "Took 5.1s\n", Final, Final,
            "Took 5.1s\n", isError: false, finished: true);
        Assert.Equal("Took 5.1s\nintervening content\n", buffer.GetRetainedText());
        Assert.Equal("Took 5.1s\nintervening content\n", string.Concat(buffer.CaptureSnapshot().Select(chunk => chunk.Text)));
    }

    [Fact]
    public void InitialProgressShowsElapsedWithoutNestedCallsAndFinalDurationStopsRedraws()
    {
        var clock = new ManualTimeProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 20,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false, timeProvider: clock);
        var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
        transcript.Render(new("tool_execution_started", Tool: "codemode", OperationId: "script"));
        transcript.Render(new("tool_execution_update", Text: "", Tool: "codemode", OperationId: "script"));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Contains("Elapsed 3.0s", Frame(output));

        var finished = JsonSerializer.Deserialize<AgentLifecycleEvent>("""
            {"Type":"tool_execution_finished","Tool":"codemode","OperationId":"script","Text":"done","DurationMs":5100}
            """)!;
        transcript.Render(finished);
        Assert.Contains("Took 5.1s", Frame(output));
        Assert.DoesNotContain("Elapsed", Frame(output));
        var finalLength = output.GetStringBuilder().Length;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(finalLength, output.GetStringBuilder().Length);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public void ReloadedPiToolResultUsesItsRecordedDurationAfterNativePersistence()
    {
        var imported = PiJsonlSessionInterchange.Import("""
            {"type":"session","version":3,"id":"timer","cwd":"/tmp"}
            {"type":"message","id":"user","parentId":null,"timestamp":"2026-10-13T18:00:00.000Z","message":{"role":"user","content":"run"}}
            {"type":"message","id":"call","parentId":"user","timestamp":"2026-10-13T18:00:01.000Z","message":{"role":"assistant","content":[{"type":"toolCall","id":"script","name":"codemode","arguments":{"code":"return 42"}}]}}
            {"type":"message","id":"result","parentId":"call","timestamp":"2026-10-13T18:00:02.000Z","message":{"role":"toolResult","toolCallId":"script","toolName":"codemode","content":[{"type":"text","text":"42"}],"isError":false,"durationMs":5100}}
            """);
        var restored = ConversationSession.Parse(imported.ToJson());
        using var shortOutput = new StringWriter();
        using var shortError = new StringWriter();
        using (var shortScreen = new TerminalScreen(shortOutput, shortError, () => 80, () => 20))
        {
            new InteractiveTranscript(shortScreen.Output, shortScreen.Error, screen: shortScreen).LoadHistory(restored);
            Assert.Contains("Took 5.1s", Frame(shortOutput));
            Assert.DoesNotContain("Elapsed", Frame(shortOutput));
        }
        var text = new string('x', 2000);
        restored.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("long-script", "codemode", new Dictionary<string, object?> { ["code"] = "return long" })]));
        var longResult = new FunctionResultContent("long-script", text);
        ToolExecutionDuration.Set(longResult, 12300);
        restored.Append(new ChatMessage(ChatRole.Tool, [longResult]));
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 20);
        new InteractiveTranscript(screen.Output, screen.Error, screen: screen).LoadHistory(restored);
        Assert.Contains("Took 12.3s", Frame(output));
        Assert.DoesNotContain("Elapsed", Frame(output));
    }

    private static string Frame(StringWriter output) =>
        TerminalOutputFrameReader.Read(output.ToString(), rows: 20, columns: 80)[^1].Screen;
}
