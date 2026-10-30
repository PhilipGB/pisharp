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

public sealed class RpcExtensionControlShutdownTests
{
    [Fact]
    public async Task ExtensionShutdownReturnsFromRpcServeWithoutClosingInputAndIsIdempotent()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-rpc-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var channel = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var shutdownCommandCalls = 0;
            adapter.AddCommand("shutdown", (_, context, _) =>
            {
                context.RequestShutdown();
                context.RequestShutdown();
                Interlocked.Increment(ref shutdownCommandCalls);
                return Task.FromResult("shutdown requested");
            });
            adapter.Close();

            var run = await ConversationRun.OpenAsync(new PiAgent(new EmptyClient(), new CodingTools(workingDirectory)),
                new ConversationSession(workingDirectory, "fixture", null));
            var service = new RpcMode(new CommandReader(channel.Reader), output, run,
                extensions: registration,
                createCommandControlOperations: (abort, cancel, shutdown, _) =>
                    new PiSharpExtensionCommandControlOperations(
                        static () => [], static () => [], static () => [], static () => null,
                        static _ => { },
                        static (_, _, _) => Task.FromResult(new PiSharpModelSelectionResult(
                            PiSharpModelSelectionStatus.UnknownModel)),
                        static () => PiSharpThinkingLevel.Off,
                        static (level, _) => Task.FromResult(level), abort,
                        static _ => Task.FromResult(new PiSharpCompactionResult(PiSharpCompactionStatus.NotNeeded)),
                        shutdown));
            var serving = service.ServeAsync();

            channel.Writer.TryWrite("{\"id\":\"shutdown\",\"type\":\"prompt\",\"message\":\"/shutdown\"}");

            await serving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, shutdownCommandCalls);
            Assert.False(channel.Reader.Completion.IsCompleted);
        }
        finally
        {
            channel.Writer.TryComplete();
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class EmptyClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unused");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
