using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class PiSharpExtensionCompactionTests
{
    [Fact]
    public async Task PublicCommandCompactionUsesTheCurrentSessionCompactionMachinery()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-extension-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, "first"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "answer one"));
            conversation.Append(new ChatMessage(ChatRole.User, "second"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
            var client = new SummaryClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(workingDirectory)), conversation);
            var operations = new PiSharpExtensionCommandControlOperations(
                static () => [], static () => [], static () => [], static () => null,
                static _ => { },
                static (_, _, _) => Task.FromResult(new PiSharpModelSelectionResult(PiSharpModelSelectionStatus.UnknownModel)),
                static () => PiSharpThinkingLevel.Off,
                static (level, _) => Task.FromResult(level),
                static () => { },
                async token =>
                {
                    try
                    {
                        var result = await run.CompactWithResultAsync(cancellationToken: token);
                        return new PiSharpCompactionResult(result is null
                            ? PiSharpCompactionStatus.NotNeeded
                            : PiSharpCompactionStatus.Compacted);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return new PiSharpCompactionResult(PiSharpCompactionStatus.Cancelled);
                    }
                    catch (Exception error)
                    {
                        return new PiSharpCompactionResult(PiSharpCompactionStatus.Failed, error.Message);
                    }
                }, static () => { });
            using var context = new PiSharpExtensionCommandContext(workingDirectory, true, false, false,
                static (_, _) => Task.CompletedTask,
                static (_, _, _, _) => Task.CompletedTask,
                operations);

            var result = await context.CompactCurrentSessionAsync();

            Assert.Equal(PiSharpCompactionStatus.Compacted, result.Status);
            Assert.Equal(1, client.SummaryRequests);
            Assert.Contains(conversation.Tree.ActivePath(), entry => entry.Type == "compaction");
            Assert.Equal(4, conversation.ActiveMessages().Count);
            Assert.Contains("compacted into the following summary", conversation.ContextMessages()[0].Text);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    private sealed class SummaryClient : IChatClient
    {
        public int SummaryRequests { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SummaryRequests++;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Earlier context summary")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
