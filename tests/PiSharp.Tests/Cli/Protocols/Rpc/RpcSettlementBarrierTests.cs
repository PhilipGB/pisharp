using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using static PiSharp.Tests.RpcTestInputOutput;

namespace PiSharp.Tests;

public sealed class RpcSettlementBarrierTests
{
    [Fact]
    public async Task SettledIsNotAdvertisedBeforeFinalSaveCompletes()
    {
        var commands = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = await ConversationRun.OpenAsync(new PiAgent(new ReplyClient(), new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var serving = new RpcMode(new CommandReader(commands.Reader), output, run, save: async _ =>
        {
            saveStarted.TrySetResult();
            await releaseSave.Task;
        }).ServeAsync();
        try
        {
            commands.Writer.TryWrite("{\"id\":\"first\",\"type\":\"prompt\",\"message\":\"first\"}");
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(output.Lines(), line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
            releaseSave.TrySetResult();
            await WaitForAsync(output, "agent_settled");
        }
        finally
        {
            releaseSave.TrySetResult();
            commands.Writer.TryComplete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FailedFinalSaveIsReportedBeforeSettlementAndAllowsNextPrompt()
    {
        var commands = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var saves = 0;
        var run = await ConversationRun.OpenAsync(new PiAgent(new ReplyClient(), new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var serving = new RpcMode(new CommandReader(commands.Reader), output, run, save: _ =>
        {
            if (Interlocked.Increment(ref saves) == 1) throw new IOException("fixture save failure");
            return Task.CompletedTask;
        }).ServeAsync();
        try
        {
            commands.Writer.TryWrite("{\"id\":\"first\",\"type\":\"prompt\",\"message\":\"first\"}");
            await WaitForAsync(output, "agent_settled");
            var lines = output.Lines().ToList();
            var error = lines.FindIndex(line => line.Contains("fixture save failure", StringComparison.Ordinal));
            var settled = lines.FindIndex(line => line.Contains("\"type\":\"agent_settled\"", StringComparison.Ordinal));
            Assert.True(error >= 0 && error < settled, string.Join(Environment.NewLine, lines));

            commands.Writer.TryWrite("{\"id\":\"next\",\"type\":\"prompt\",\"message\":\"next\"}");
            await WaitForAsync(output, "\"id\":\"next\"");
            using var response = JsonDocument.Parse(output.Lines().Single(line => line.Contains("\"id\":\"next\"", StringComparison.Ordinal)));
            Assert.True(response.RootElement.GetProperty("success").GetBoolean(), response.RootElement.GetRawText());
        }
        finally
        {
            commands.Writer.TryComplete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task NextPromptDoesNotBecomeBusyErrorAfterExternalSettledSignal()
    {
        var commands = Channel.CreateUnbounded<string>();
        using var output = new SettledWriter();
        var run = await ConversationRun.OpenAsync(new PiAgent(new ReplyClient(), new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var serving = new RpcMode(new CommandReader(commands.Reader), output, run, getCurrentResources: () =>
        {
            if (output.Settled.Task.IsCompleted) output.NextRead.TrySetResult();
            return null;
        }).ServeAsync();
        try
        {
            commands.Writer.TryWrite("{\"id\":\"first\",\"type\":\"prompt\",\"message\":\"first\"}");
            await output.Settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            commands.Writer.TryWrite("{\"id\":\"next\",\"type\":\"prompt\",\"message\":\"next\"}");
            await output.NextRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            output.Release.TrySetResult();
            var next = await output.NextResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(next.GetProperty("success").GetBoolean(), next.GetRawText());
        }
        finally
        {
            output.Release.TrySetResult();
            commands.Writer.TryComplete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class SettledWriter : StringWriter
    {
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NextRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<JsonElement> NextResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _settledCount;

        public override async Task WriteAsync(string? value)
        {
            using var document = JsonDocument.Parse(value!);
            var record = document.RootElement;
            if (record.GetProperty("type").GetString() == "agent_settled" && Interlocked.Increment(ref _settledCount) == 1)
            {
                Settled.TrySetResult();
                await Release.Task;
            }
            if (record.TryGetProperty("id", out var id))
            {
                if (id.GetString() == "next") NextResponse.TrySetResult(record.Clone());
            }
        }
    }

    private sealed class ReplyClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return new ChatResponseUpdate(ChatRole.Assistant, "reply"); await Task.CompletedTask; }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
