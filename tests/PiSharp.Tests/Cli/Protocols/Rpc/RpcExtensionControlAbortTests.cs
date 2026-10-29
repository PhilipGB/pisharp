using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Protocols;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using static PiSharp.Tests.RpcTestInputOutput;

namespace PiSharp.Tests;

public sealed class RpcExtensionControlAbortTests
{
    [Fact]
    public async Task ExtensionAbortIsIdleSafeSettlesTheActiveRunAndAllowsTheNextPrompt()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-rpc-extension-abort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        var client = new AbortRecoveryClient();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            adapter.AddCommand("abort", (_, context, _) =>
            {
                context.AbortCurrentRun();
                return Task.FromResult("abort requested");
            });
            adapter.Close();

            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(workingDirectory)),
                new ConversationSession(workingDirectory, "fixture", null));
            var service = new RpcMode(new CommandReader(channel.Reader), output, run,
                extensions: registration,
                createCommandControlOperations: (abort, cancel, shutdown, _) => new(
                    static () => [], static () => [], static () => [], static () => null,
                    static _ => { },
                    static (_, _, _) => Task.FromResult(new PiSharpModelSelectionResult(PiSharpModelSelectionStatus.UnknownModel)),
                    static () => PiSharpThinkingLevel.Off,
                    static (level, _) => Task.FromResult(level), abort,
                    static _ => Task.FromResult(new PiSharpCompactionResult(PiSharpCompactionStatus.NotNeeded)), shutdown));
            var serving = service.ServeAsync();

            channel.Writer.TryWrite("{\"id\":\"idle-abort\",\"type\":\"prompt\",\"message\":\"/abort\"}");
            await WaitForAsync(output, "\"id\":\"idle-abort\"");
            Assert.False(client.FirstRequestStarted.Task.IsCompleted);

            channel.Writer.TryWrite("{\"id\":\"first\",\"type\":\"prompt\",\"message\":\"wait for abort\"}");
            await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            channel.Writer.TryWrite("{\"id\":\"abort-active\",\"type\":\"prompt\",\"message\":\"/abort\"}");
            await WaitForAsync(output, "\"id\":\"abort-active\"");
            await client.FirstRequestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForAsync(output, "agent_settled");

            var previousLineCount = output.LineCount;
            channel.Writer.TryWrite("{\"id\":\"second\",\"type\":\"prompt\",\"message\":\"recover\"}");
            await client.SecondRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForAsync(output, "\"id\":\"second\"");
            await output.WaitForLineAfterAsync("agent_settled", previousLineCount);
            channel.Writer.TryComplete();
            await serving.WaitAsync(TimeSpan.FromSeconds(5));

            var responses = output.Lines().Where(line => line.Contains("\"type\":\"response\"", StringComparison.Ordinal))
                .Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                foreach (var id in new[] { "idle-abort", "first", "abort-active", "second" })
                {
                    var response = Assert.Single(responses, item => item.RootElement.GetProperty("id").GetString() == id);
                    Assert.True(response.RootElement.GetProperty("success").GetBoolean(), id);
                }
                Assert.Contains(output.Lines(), line => line.Contains("recovered", StringComparison.Ordinal));
            }
            finally { foreach (var response in responses) response.Dispose(); }
        }
        finally
        {
            channel.Writer.TryComplete();
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class AbortRecoveryClient : IChatClient
    {
        private int _requestCount;
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                FirstRequestStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    FirstRequestCancelled.TrySetResult();
                    throw;
                }
                yield break;
            }

            SecondRequestStarted.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
