using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class DurableExecutionTests
{
    [Fact]
    public async Task CrashAfterSideEffectLeavesRecordedOutcomeAndNeverReinvokesOnRecovery()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var session = new ConversationSession(cwd, "fixture", null);
            var path = store.NewPath(session);
            var checkpoints = new List<string>();
            async Task Save(CancellationToken token)
            {
                checkpoints.Add(session.Tree.Entries.Last().Type);
                await store.SaveAsync(session, path, token);
            }
            var run = await ConversationRun.OpenAsync(new PiAgent(new WriteThenBlockClient(), new CodingTools(cwd)),
                session, save: Save);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in run.RunStreamingAsync("create a file", cancel.Token)) { }
            });
            Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(cwd, "result.txt")));
            Assert.Equal(["run_started", "chat", "tool_intent", "tool_outcome", "run_finished"], checkpoints);
            var acceptedPrompt = session.Tree.Entries.Single(node => node.Type == "chat" &&
                ConversationSession.RestoreEntry(node).Text == "create a file");
            Assert.True(session.Tree.Entries.ToList().FindIndex(node => node.Id == acceptedPrompt.Id) <
                session.Tree.Entries.ToList().FindIndex(node => node.Type == "tool_intent"));
            var document = await store.LoadAsync(path);
            Assert.Contains(document.Tree.Entries, node => node.Type == "tool_outcome");
            Assert.True(document.RecoverIncomplete()); // A settled but failed continuation still needs a no-replay warning.
            Assert.False(document.RecoverIncomplete());
            Assert.Contains("No tool outcome is unknown", document.ActiveMessages().Last().Text);
            // Simulate SIGKILL immediately after the side-effect checkpoint, before run_finished.
            document.Tree.Select(document.Tree.Entries.Single(node => node.Type == "tool_outcome").Id);
            await store.SaveAsync(document, path);
            var crashed = await store.LoadAsync(path);
            Assert.True(crashed.RecoverIncomplete());
            Assert.False(crashed.RecoverIncomplete());
            Assert.Contains("No tool outcome is unknown", crashed.ActiveMessages().Last().Text);
            await store.SaveAsync(crashed, path);
            var client = new ObserveClient();
            var resumedConversation = await store.LoadAsync(path);
            var resumed = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)),
                resumedConversation, save: token => store.SaveAsync(resumedConversation, path, token));
            await foreach (var _ in resumed.RunStreamingAsync("continue")) { }
            Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(cwd, "result.txt")));
            Assert.Contains("Recovery notice", client.Requests.Single().Last(m => m.Role == ChatRole.User && m.Text!.Contains("Recovery")).Text);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task CrashBetweenToolIntentAndResultIsExplicitlyUnknown()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-intent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var session = new ConversationSession(cwd, "fixture", null);
            var runId = Guid.NewGuid().ToString("N");
            session.Tree.Append("run_started", System.Text.Json.JsonSerializer.SerializeToElement(new { runId, prompt = "edit data" }));
            session.Tree.Append("tool_intent", System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                runId,
                operationId = "op1",
                name = "bash",
                arguments = new { command = "echo effect >> log" }
            }));
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(session);
            await store.SaveAsync(session, path);
            var loaded = await store.LoadAsync(path);
            var client = new ObserveClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(cwd)), loaded,
                save: token => store.SaveAsync(loaded, path, token));
            Assert.Contains("Outcome UNKNOWN for bash", loaded.ActiveMessages().Single().Text);
            Assert.Equal("bash", loaded.Tree.Entries.Single(node => node.Type == "tool_intent").Payload.GetProperty("name").GetString());
            Assert.False(File.Exists(Path.Combine(cwd, "log")));
            await foreach (var _ in run.RunStreamingAsync("check status")) { }
            Assert.False(File.Exists(Path.Combine(cwd, "log")));
            Assert.Contains("Outcome UNKNOWN", client.Requests.Single().Single(m => m.Text?.Contains("Recovery notice") == true).Text);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public void FailedSettledRunWithUnknownToolOutcomeRequiresRecoveryWarning()
    {
        var session = new ConversationSession(Path.GetTempPath(), "fixture", null);
        var runId = Guid.NewGuid().ToString("N");
        session.Tree.Append("run_started", System.Text.Json.JsonSerializer.SerializeToElement(new { runId, prompt = "write" }));
        session.Tree.Append("tool_intent", System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            runId,
            operationId = "unknown",
            name = "write",
            arguments = new { path = "file.txt", content = "maybe" }
        }));
        session.Tree.Append("run_finished", System.Text.Json.JsonSerializer.SerializeToElement(new { runId, completed = false }));
        var persisted = ConversationSession.Parse(session.ToJson());
        Assert.True(persisted.RecoverIncomplete());
        Assert.Contains("Outcome UNKNOWN for write", persisted.ActiveMessages().Last().Text);
        Assert.False(persisted.RecoverIncomplete());
    }

    [Fact]
    public async Task NoToolInvokedIfIntentCannotBeDurablyRecorded()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var session = new ConversationSession(cwd, "fixture", null);
            var path = store.NewPath(session);
            async Task Save(CancellationToken token)
            {
                if (session.Tree.Entries.Last().Type == "tool_intent") throw new IOException("disk unavailable");
                await store.SaveAsync(session, path, token);
            }
            var run = await ConversationRun.OpenAsync(new PiAgent(new ObserveClient(withTool: true), new CodingTools(cwd)),
                session, save: Save);
            await foreach (var _ in run.RunStreamingAsync("write")) { }
            Assert.False(File.Exists(Path.Combine(cwd, "result.txt")));
            var persisted = await store.LoadAsync(path);
            Assert.Contains(persisted.Tree.Entries, node => node.Type == "tool_skipped");
            persisted.Tree.Select(persisted.Tree.Entries.Single(node => node.Type == "tool_skipped").Id);
            Assert.True(persisted.RecoverIncomplete());
            Assert.Contains("No tool outcome is unknown", persisted.ActiveMessages().Last().Text);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private sealed class WriteThenBlockClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("once", "write", new Dictionary<string, object?> { ["path"] = "result.txt", ["content"] = "made" })]);
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ObserveClient(bool withTool = false) : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            if (withTool && !messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any())
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("once", "write", new Dictionary<string, object?> { ["path"] = "result.txt", ["content"] = "made" })]);
            else yield return new ChatResponseUpdate(ChatRole.Assistant, "okay");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
