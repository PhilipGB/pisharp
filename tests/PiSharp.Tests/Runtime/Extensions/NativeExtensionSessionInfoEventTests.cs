using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionSessionInfoEventTests
{
    [Fact]
    public async Task NameEventUsesCommittedNormalizedNameAndSkipsUnchangedOrFailedCommits()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-session-info-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<(string? Name, string? ContextName)>();
            adapter.AddSessionInfoChangedHandler((eventData, context, _) =>
            {
                observed.Add((eventData.Name, context.Session?.SessionName));
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var failSave = false;
            var agent = new PiAgent(new StubClient(), new CodingTools(workingDirectory),
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation,
                save: _ => failSave ? Task.FromException(new IOException("save failed")) : Task.CompletedTask);

            await run.SetSessionNameAsync("  alpha\r\nbeta  ");
            Assert.Equal("alpha beta", conversation.Name);
            Assert.Equal([("alpha beta", "alpha beta")], observed);

            await run.SetSessionNameAsync("alpha beta");
            Assert.Single(observed);

            await run.SetSessionNameAsync("  \r\n ");
            Assert.Null(conversation.Name);
            Assert.Equal([("alpha beta", "alpha beta"), (null, null)], observed);

            failSave = true;
            await Assert.ThrowsAsync<IOException>(() => run.SetSessionNameAsync("uncommitted"));
            Assert.Null(conversation.Name);
            Assert.Equal(2, observed.Count);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    private sealed class StubClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "unused")]));

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
