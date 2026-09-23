using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ConversationSessionTests
{
    [Fact]
    public async Task CompletedToolTurnSurvivesRestartWithoutRepeatingSideEffect()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-canonical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var canonical = new ConversationSession(cwd, "fixture", null);
            var initial = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), canonical);
            await foreach (var _ in initial.RunStreamingAsync("Make file")) { }
            var file = Path.Combine(cwd, "generated.txt");
            Assert.Equal("made", await File.ReadAllTextAsync(file));
            var path = store.NewPath(canonical);
            await store.SaveAsync(canonical, path);
            Assert.Equal(path, store.MostRecentPath());
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            var afterRestart = await store.LoadAsync(path);
            var client = new ScriptedClient();
            var resumed = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), afterRestart);
            await foreach (var _ in resumed.RunStreamingAsync("What happened?")) { }
            Assert.Single(client.Requests);
            Assert.Contains(client.Requests[0].SelectMany(m => m.Contents), c => c is FunctionCallContent { CallId: "call-1" });
            Assert.Contains(client.Requests[0].SelectMany(m => m.Contents), c => c is FunctionResultContent { CallId: "call-1" });
            Assert.Equal("made", await File.ReadAllTextAsync(file));
            Assert.Contains(afterRestart.ActiveMessages(), m => m.Text == "What happened?");
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task BranchSelectionSurvivesSaveAndRestartEvenIfLastEntryIsElsewhere()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var canonical = new ConversationSession(cwd, "fixture", null);
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var run = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), canonical);
            await foreach (var _ in run.RunStreamingAsync("root")) { }
            var root = canonical.Tree.HeadId!;
            await foreach (var _ in run.RunStreamingAsync("abandoned")) { }
            await run.SelectAsync(root);
            await foreach (var _ in run.RunStreamingAsync("alternate")) { }
            var alternate = canonical.Tree.HeadId!;
            await run.SelectAsync(root); // Intentional selection differs from last appended entry.
            var path = store.NewPath(canonical);
            await store.SaveAsync(canonical, path);
            var loaded = await store.LoadAsync(path);
            Assert.Equal(root, loaded.Tree.HeadId);
            Assert.NotEqual(alternate, loaded.Tree.HeadId);
            var client = new ScriptedClient();
            var restored = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), loaded);
            var countBefore = loaded.Tree.Entries.Count;
            await foreach (var _ in restored.RunStreamingAsync("continue root")) { }
            var history = string.Join(" ", client.Requests.Single().Select(m => m.Text));
            Assert.Contains("root", history);
            Assert.Contains("continue root", history);
            Assert.DoesNotContain("abandoned", history);
            Assert.DoesNotContain("alternate", history);
            Assert.Equal(root, loaded.Tree.Entries[countBefore].ParentId);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task CancelledTurnRecordsInterruptionAndCanResume()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-interrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var canonical = new ConversationSession(cwd, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new BlockingClient(), new CodingTools(cwd)), canonical);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in run.RunStreamingAsync("interrupt me", cancel.Token)) { }
            });
            Assert.Equal("interrupted", canonical.Tree.Entries.Last().Type);
            var path = store.NewPath(canonical);
            await store.SaveAsync(canonical, path);
            var loaded = await store.LoadAsync(path);
            var restored = await ConversationRun.OpenAsync(new PiAgent(new ScriptedClient(), new CodingTools(cwd)), loaded);
            await foreach (var _ in restored.RunStreamingAsync("next")) { }
            Assert.Contains(loaded.ActiveMessages(), m => m.Text == "next");
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private sealed class BlockingClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ScriptedClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var captured = messages.ToArray();
            Requests.Add(captured);
            if (captured.LastOrDefault(m => m.Role == ChatRole.User)?.Text == "Make file" &&
                !captured.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "write", new Dictionary<string, object?> { ["path"] = "generated.txt", ["content"] = "made" })]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "ack");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
