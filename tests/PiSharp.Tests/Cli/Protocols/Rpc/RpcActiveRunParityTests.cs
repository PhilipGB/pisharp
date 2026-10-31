using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

using static PiSharp.Tests.RpcTestInputOutput;

namespace PiSharp.Tests;

/// <summary>
/// Pi-parity for the RPC lifecycle of active runs: agent_start/agent_end
/// boundaries around failed-turn continuations and queue drain ordering.
/// Oracle: pinned Pi (tools/parity/pi-reference.txt).
/// </summary>
public sealed class RpcActiveRunParityTests
{
    [Fact]
    public async Task FailedTurnWithQueuedSteeringRestartsAgentLifecycleBeforeTheSteeringTurn()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new FailFirstThenAnswerClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())),
            new ConversationSession(Path.GetTempPath(), "fixture", null));
        var serving = new RpcMode(new CommandReader(channel.Reader), output, run).ServeAsync();

        channel.Writer.TryWrite("{\"id\":\"go\",\"type\":\"prompt\",\"message\":\"GO\"}");
        await client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.TryWrite("{\"id\":\"s1\",\"type\":\"steer\",\"message\":\"S1\"}");
        client.Release.TrySetResult();
        await WaitForAsync(output, "agent_settled");
        channel.Writer.Complete();
        await serving.WaitAsync(TimeSpan.FromSeconds(5));

        var lifecycle = output.Lines()
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var type))
                {
                    var name = type.GetString() ?? string.Empty;
                    if (name == "message_start" &&
                        root.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("role", out var role))
                        return "message:" + role.GetString();
                    return name;
                }
                return null;
            })
            .Where(type => type is "agent_start" or "agent_end" or "turn_start" or "turn_end"
                or "queue_update" or "agent_settled" or "message:user" or "message:assistant")
            .ToList();

        Assert.Equal(
        [
            "agent_start", "turn_start", "message:user", "message:assistant", "turn_end",
            "agent_end",
            "agent_start", "turn_start", "queue_update", "message:user", "message:assistant", "turn_end",
            "agent_end", "agent_settled"
        ], lifecycle);
        Assert.Equal(2, client.Requests);
    }

    [Fact]
    public async Task RestartedSessionDoesNotReEmitUnchangedSystemMessage()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-rpc-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var systemMessage = new JsonObject { ["content"] = "system prompt" };
            var firstChannel = Channel.CreateUnbounded<string>();
            using var firstOutput = new LockedWriter();
            var firstSession = new ConversationSession(cwd, "fixture", null);
            var firstRun = await ConversationRun.OpenAsync(new PiAgent(new TextClient(), new CodingTools(cwd)),
                firstSession);
            var firstServing = new RpcMode(new CommandReader(firstChannel.Reader), firstOutput, firstRun,
                getSystemMessage: () => systemMessage).ServeAsync();
            firstChannel.Writer.TryWrite("{\"id\":\"go\",\"type\":\"prompt\",\"message\":\"GO\"}");
            await WaitForAsync(firstOutput, "agent_settled");
            firstChannel.Writer.Complete();
            await firstServing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains(firstOutput.Lines(), line =>
                line.Contains("\"type\":\"message_start\"", StringComparison.Ordinal) &&
                line.Contains("\"role\":\"system\"", StringComparison.Ordinal));

            var sessionPath = Directory.GetFiles(cwd, "*.session.json").Single(path =>
                !Path.GetFileName(path).StartsWith("fork-"));
            var restored = ConversationSession.Parse(File.ReadAllText(sessionPath));
            var secondChannel = Channel.CreateUnbounded<string>();
            using var secondOutput = new LockedWriter();
            var secondRun = await ConversationRun.OpenAsync(new PiAgent(new TextClient(), new CodingTools(cwd)),
                restored);
            var secondServing = new RpcMode(new CommandReader(secondChannel.Reader), secondOutput, secondRun,
                getSystemMessage: () => systemMessage).ServeAsync();
            secondChannel.Writer.TryWrite("{\"id\":\"again\",\"type\":\"prompt\",\"message\":\"AGAIN\"}");
            await WaitForAsync(secondOutput, "agent_settled");
            secondChannel.Writer.Complete();
            await secondServing.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.DoesNotContain(secondOutput.Lines(), line =>
                line.Contains("\"type\":\"message_start\"", StringComparison.Ordinal) &&
                line.Contains("\"role\":\"system\"", StringComparison.Ordinal));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private sealed class FailFirstThenAnswerClient : IChatClient
    {
        public int Requests { get; private set; }
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ++Requests;
            if (Requests == 1)
            {
                RequestStarted.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("fixture provider failure");
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                messages.Any(message => message.Text == "S1") ? "S1-OK" : "NEXT-OK");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TextClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "OK");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
