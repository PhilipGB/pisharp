using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Tui;
using PiSharp.Runtime;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Tests;

if (Console.IsOutputRedirected) throw new InvalidOperationException("Live timer probe requires a PTY.");
var capture = new CapturingWriter(Console.Out);
var events = new List<AgentLifecycleEvent>();
using (var screen = new TerminalScreen(capture, Console.Error, () => 120, () => 24))
{
    var transcript = new InteractiveTranscript(screen.Output, screen.Error, screen: screen);
    var registration = new ExtensionRegistration();
    registration.AddTool(new PiSharpToolRegistration(AIFunctionFactory.Create(async (CancellationToken token) =>
    {
        await Task.Delay(1300, token);
        return "held";
    }, name: "hold"), ToolExposure.CodeMode));
    CodemodeBuiltin.Configure(registration);
    using var client = new ScriptClient();
    var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), selectedTools: ["codemode"],
        extensionToolRegistrations: registration.ToolDefinitions);
    var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
    var run = await ConversationRun.OpenAsync(agent, session);
    await foreach (var item in run.RunEventsAsync("run"))
    {
        events.Add(item);
        transcript.Render(item);
    }
    transcript.FinishTurn();
}
var frames = TerminalOutputFrameReader.Read(capture.Text, rows: 24, columns: 120);
var finished = events.Single(item => item.Type == "tool_execution_finished" && item.Tool == "codemode");
var progressIndex = events.FindIndex(item => item.Type == "tool_execution_update" && item.Tool == "codemode");
var nestedIndex = events.FindIndex(item => item.Type == "tool_execution_started" && item.Tool == "hold");
var total = "Took " + ToolDurationText.Format(finished.DurationMs!.Value);
Console.WriteLine("PISHARP_CODEMODE_TIMER=" + JsonSerializer.Serialize(new
{
    initialBeforeNested = progressIndex >= 0 && progressIndex < nestedIndex,
    initialDisplayed = frames.Any(frame => frame.Screen.Contains("Elapsed 0.0s", StringComparison.Ordinal)),
    elapsedRedrawn = frames.Any(frame => frame.Screen.Contains("Elapsed 1.0s", StringComparison.Ordinal)),
    finalRecorded = frames[^1].Screen.Contains(total, StringComparison.Ordinal),
    noFinalElapsed = !frames[^1].Screen.Contains("Elapsed", StringComparison.Ordinal),
    successful = finished.IsError != true,
    durationRecorded = finished.DurationMs >= 1300
}));

sealed class CapturingWriter(TextWriter inner) : TextWriter
{
    private readonly StringBuilder _capture = new();
    public string Text => _capture.ToString();
    public override Encoding Encoding => inner.Encoding;
    public override void Write(char value) { _capture.Append(value); inner.Write(value); }
    public override void Write(string? value) { _capture.Append(value); inner.Write(value); }
    public override void Flush() => inner.Flush();
}

sealed class ScriptClient : IChatClient
{
    private int _requests;
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => Task.FromResult(Next());
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in Next().ToChatResponseUpdates()) yield return update;
        await Task.CompletedTask;
    }
    private ChatResponse Next() => Interlocked.Increment(ref _requests) == 1
        ? new(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("script", "codemode", new Dictionary<string, object?> { ["code"] = "await tools.hold({}); return 42;" })]))
        { FinishReason = ChatFinishReason.ToolCalls }
        : new(new ChatMessage(ChatRole.Assistant, "done")) { FinishReason = ChatFinishReason.Stop };
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
