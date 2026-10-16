using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;
using PiSharp.Tests;

var durations = JsonSerializer.Deserialize<double[]>(File.ReadAllText(args[0]))!;
var clock = new ManualTimeProvider();
using var output = new StringWriter();
using var error = new StringWriter();
using var screen = new TerminalScreen(output, error, () => 120, () => 24,
    new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false, timeProvider: clock);
var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
transcript.Render(new("tool_execution_started", Tool: "codemode", OperationId: "script"));
transcript.Render(new("tool_execution_update", Text: "", Tool: "codemode", OperationId: "script"));
var initial = TimerLine(output);
var initialLength = output.GetStringBuilder().Length;
clock.Advance(TimeSpan.FromSeconds(3));
var running = TimerLine(output);
var refreshed = output.GetStringBuilder().Length > initialLength;
transcript.Render(new("tool_execution_finished", Text: "done", Tool: "codemode", OperationId: "script") { DurationMs = 5100 });
var final = TimerLine(output);
var finalLength = output.GetStringBuilder().Length;
clock.Advance(TimeSpan.FromSeconds(10));
var settled = output.GetStringBuilder().Length == finalLength;

var reloaded = AnthropicSession();
transcript.LoadHistory(ConversationSession.Parse(reloaded.ToJson()));
var reload = TimerLine(output);
var timers = new TerminalToolTimers(clock, () => { });
timers.Start("partial-error");
_ = timers.Progress("partial-error", false);
clock.Advance(TimeSpan.FromSeconds(3));
var frozenError = timers.Progress("partial-error", true);
clock.Advance(TimeSpan.FromSeconds(2));
var frozenErrorLater = timers.Progress("partial-error", false);
var finalError = "Took " + ToolDurationText.Format(timers.Complete("partial-error", null)!.Value);
Console.WriteLine(JsonSerializer.Serialize(new
{
    formatted = durations.Select(ToolDurationText.Format).ToArray(),
    row = new { initial, running, refreshed, final, settled, reload },
    partialError = new { frozenError, frozenErrorLater, finalError }
}));

static string? TimerLine(StringWriter output)
{
    var frame = TerminalOutputFrameReader.Read(output.ToString(), rows: 24, columns: 120)[^1].Screen;
    return frame.Split('\n').Select(line => line.Trim()).LastOrDefault(line => Regex.IsMatch(line, "^(Elapsed|Took) "));
}

static ConversationSession AnthropicSession() => PiJsonlSessionInterchange.Import("""
    {"type":"session","version":3,"id":"timer","cwd":"/tmp"}
    {"type":"message","id":"user","parentId":null,"timestamp":"2026-10-13T18:00:00.000Z","message":{"role":"user","content":"run"}}
    {"type":"message","id":"call","parentId":"user","timestamp":"2026-10-13T18:00:01.000Z","message":{"role":"assistant","content":[{"type":"toolCall","id":"script","name":"codemode","arguments":{"code":"return 42"}}]}}
    {"type":"message","id":"result","parentId":"call","timestamp":"2026-10-13T18:00:02.000Z","message":{"role":"toolResult","toolCallId":"script","toolName":"codemode","content":[{"type":"text","text":"42"}],"isError":false,"durationMs":5100}}
    """);
