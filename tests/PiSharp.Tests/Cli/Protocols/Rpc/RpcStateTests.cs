using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class RpcStateTests
{
    [Fact]
    public async Task SetAutoCompactionChangesActiveRunBehaviorAndState()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new ConcurrentTextWriter();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, new string('a', 3000)));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "earlier answer"));
        conversation.Append(new ChatMessage(ChatRole.User, "latest"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "latest answer"));
        var client = new BlockingSummaryClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), conversation,
            autoCompaction: new AutoCompactionPolicy(1800, 300), autoCompactionEnabled: false);
        var service = new RpcMode(new CommandChannelReader(channel.Reader), output, run);
        var serving = service.ServeAsync();

        try
        {
            channel.Writer.TryWrite("{\"id\":\"disabled-state\",\"type\":\"get_state\"}");
            await output.WaitForLineAsync("\"id\":\"disabled-state\"");
            using (var disabled = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
                       line.Contains("\"id\":\"disabled-state\"", StringComparison.Ordinal))))
                Assert.False(disabled.RootElement.GetProperty("data").GetProperty("autoCompactionEnabled").GetBoolean());

            channel.Writer.TryWrite("{\"id\":\"prompt-disabled\",\"type\":\"prompt\",\"message\":\"continue without compaction\"}");
            await output.WaitForOccurrenceAsync("\"type\":\"agent_settled\"", 1);
            Assert.False(client.SummaryStarted.Task.IsCompleted);

            channel.Writer.TryWrite("{\"id\":\"enable\",\"type\":\"set_auto_compaction\",\"enabled\":true}");
            channel.Writer.TryWrite("{\"id\":\"enabled-state\",\"type\":\"get_state\"}");
            await output.WaitForLineAsync("\"id\":\"enabled-state\"");
            using (var enabled = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
                       line.Contains("\"id\":\"enabled-state\"", StringComparison.Ordinal))))
                Assert.True(enabled.RootElement.GetProperty("data").GetProperty("autoCompactionEnabled").GetBoolean());

            channel.Writer.TryWrite("{\"id\":\"prompt-enabled\",\"type\":\"prompt\",\"message\":\"continue with compaction\"}");
            await client.SummaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            channel.Writer.TryWrite("{\"id\":\"compacting-state\",\"type\":\"get_state\"}");
            await output.WaitForLineAsync("\"id\":\"compacting-state\"");
            using (var compacting = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
                       line.Contains("\"id\":\"compacting-state\"", StringComparison.Ordinal))))
            {
                Assert.True(compacting.RootElement.GetProperty("data").GetProperty("isCompacting").GetBoolean());
                Assert.True(compacting.RootElement.GetProperty("data").GetProperty("isStreaming").GetBoolean());
            }

            client.ReleaseSummary.TrySetResult();
            await output.WaitForOccurrenceAsync("\"type\":\"agent_settled\"", 2);
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, client.SummaryCalls);
            Assert.Equal(2, client.ProviderCalls);
        }
        finally
        {
            client.ReleaseSummary.TrySetResult();
            channel.Writer.TryComplete();
            if (!serving.IsCompleted) await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task GetStateReportsStreamingAndCompactionWhileAutomaticSummaryIsInFlight()
    {
        var channel = Channel.CreateUnbounded<string>();
        using var output = new ConcurrentTextWriter();
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, new string('a', 3000)));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "earlier answer"));
        conversation.Append(new ChatMessage(ChatRole.User, "latest"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "latest answer"));
        var client = new BlockingSummaryClient();
        var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(Path.GetTempPath())), conversation,
            autoCompaction: new AutoCompactionPolicy(1800, 300));
        var service = new RpcMode(new CommandChannelReader(channel.Reader), output, run);
        var serving = service.ServeAsync();

        try
        {
            channel.Writer.TryWrite("{\"id\":\"prompt\",\"type\":\"prompt\",\"message\":\"continue\"}");
            await client.SummaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            channel.Writer.TryWrite("{\"id\":\"state\",\"type\":\"get_state\"}");
            await output.WaitForLineAsync("\"id\":\"state\"");

            using var response = JsonDocument.Parse(Assert.Single(output.Lines(), line =>
                line.Contains("\"id\":\"state\"", StringComparison.Ordinal)));
            var state = response.RootElement.GetProperty("data");
            Assert.True(state.GetProperty("isStreaming").GetBoolean());
            Assert.True(state.GetProperty("isCompacting").GetBoolean());
            Assert.True(state.GetProperty("autoCompactionEnabled").GetBoolean());

            client.ReleaseSummary.TrySetResult();
            await output.WaitForLineAsync("\"type\":\"agent_settled\"");
            channel.Writer.Complete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            client.ReleaseSummary.TrySetResult();
            channel.Writer.TryComplete();
            if (!serving.IsCompleted) await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class CommandChannelReader(ChannelReader<string> channel) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try { return await channel.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }
    }

    private sealed class ConcurrentTextWriter : StringWriter
    {
        private readonly object _gate = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteAsync(string? value)
        {
            lock (_gate)
            {
                Write(value);
                var changed = _changed;
                _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
            }
            return Task.CompletedTask;
        }

        public async Task WaitForLineAsync(string fragment)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (ToString().Contains(fragment, StringComparison.Ordinal)) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(timeout.Token);
            }
        }

        public async Task WaitForOccurrenceAsync(string fragment, int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (ToString().Split(fragment, StringSplitOptions.None).Length - 1 >= count) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(timeout.Token);
            }
        }

        public string[] Lines()
        {
            lock (_gate) return ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    private sealed class BlockingSummaryClient : IChatClient
    {
        public TaskCompletionSource SummaryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSummary { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SummaryCalls { get; private set; }
        public int ProviderCalls { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            SummaryCalls++;
            SummaryStarted.TrySetResult();
            await ReleaseSummary.Task.WaitAsync(cancellationToken);
            return new ChatResponse([new ChatMessage(ChatRole.Assistant, "summary")]);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ProviderCalls++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
