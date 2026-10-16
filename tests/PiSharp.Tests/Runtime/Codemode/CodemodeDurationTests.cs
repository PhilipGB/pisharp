using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

[Collection("Codemode workers")]
public sealed class CodemodeDurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialProgressPrecedesCompletionAndMeasuredDurationSurvivesNativeAndPiSessions(bool fail)
    {
        using var client = new ScriptClient(fail ? "throw new Error('failure');" : "return 42;");
        var registration = new ExtensionRegistration();
        CodemodeBuiltin.Configure(registration);
        var agent = new PiAgent(client, new CodingTools(Path.GetTempPath()), selectedTools: ["codemode"],
            extensionToolRegistrations: registration.ToolDefinitions);
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var run = await ConversationRun.OpenAsync(agent, session);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("run")) events.Add(item);
        var started = events.FindIndex(item => item.Type == "tool_execution_started" && item.Tool == "codemode");
        var progress = events.FindIndex(item => item.Type == "tool_execution_update" && item.Tool == "codemode" && item.Text == "");
        var finishedIndex = events.FindIndex(item => item.Type == "tool_execution_finished" && item.Tool == "codemode");
        Assert.True(started >= 0 && progress > started && finishedIndex > progress);
        var finished = events[finishedIndex];
        Assert.Equal(fail, finished.IsError);
        Assert.NotNull(finished.DurationMs);
        Assert.True(finished.DurationMs >= 0);
        double? Duration(ConversationSession source) => ToolExecutionDuration.Get(source.ActiveMessages()
            .SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single(result => result.CallId == "script"));
        Assert.Equal(finished.DurationMs, Duration(session));
        var native = ConversationSession.Parse(session.ToJson());
        Assert.Equal(finished.DurationMs, Duration(native));
        var imported = PiJsonlSessionInterchange.Import(PiJsonlSessionInterchange.Export(native));
        Assert.Equal(finished.DurationMs, Duration(imported));
    }

    private sealed class ScriptClient(string code) : IChatClient
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
                [new FunctionCallContent("script", "codemode", new Dictionary<string, object?> { ["code"] = code })]))
            { FinishReason = ChatFinishReason.ToolCalls }
            : new(new ChatMessage(ChatRole.Assistant, "done")) { FinishReason = ChatFinishReason.Stop };
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
