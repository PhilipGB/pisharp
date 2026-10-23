using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

[Collection("Extension environment")]
public sealed class BashSessionEnvironmentTests
{
    [Fact]
    public async Task InheritedMetadataAndEarlierSessionDoNotLeakIntoDirectOrNewSessionBash()
    {
        var names = new[] { "PI_SESSION_ID", "PI_SESSION_FILE", "PI_PROVIDER", "PI_MODEL", "PI_REASONING_LEVEL" };
        var inherited = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-inherited-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, "stale-fixture-value");
            await File.WriteAllTextAsync(Path.Combine(root, "input"), "unchanged");
            var tools = new CodingTools(root);
            var firstClient = new SelectionClient();
            firstClient.Release.TrySetResult();
            var firstConversation = new ConversationSession(root, "first-model", null, "first-provider");
            var firstFile = Path.Combine(root, "first.session.json");
            var first = await ConversationRun.OpenAsync(new PiAgent(firstClient, tools), firstConversation,
                sessionFile: firstFile, provider: "first-provider", reasoningLevel: "high");
            await foreach (var _ in first.RunEventsAsync("first session")) { }
            Assert.Equal($"{firstConversation.Id}|{firstFile}|first-provider|first-model|high", firstClient.BashResult);
            var secondClient = new SelectionClient();
            secondClient.Release.TrySetResult();
            var secondConversation = new ConversationSession(root, "second-model", null);
            var second = await ConversationRun.OpenAsync(new PiAgent(secondClient, tools), secondConversation);
            await foreach (var _ in second.RunEventsAsync("new session")) { }
            Assert.Equal($"{secondConversation.Id}|unset|unset|second-model|unset", secondClient.BashResult);
            var direct = await tools.ExecuteBashAsync(SelectionClient.EnvironmentCommand);
            Assert.Equal("unset|unset|unset|unset|unset", direct.Output);
        }
        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, inherited[name]);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BashReceivesSelectionCurrentAtItsProviderTurn(bool changeModel)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var client = new SelectionClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task? active = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "input"), "unchanged");
            var conversation = new ConversationSession(root, "old-model", null, "old-provider");
            var agent = new PiAgent(client, new CodingTools(root));
            var sessionFile = Path.Combine(root, "session.json");
            var run = await ConversationRun.OpenAsync(agent, conversation, provider: "old-provider", reasoningLevel: "low", sessionFile: sessionFile);
            active = Task.Run(async () =>
            {
                await foreach (var _ in run.RunEventsAsync("inspect after selection", deadline.Token)) { }
            });
            await client.Started.Task.WaitAsync(deadline.Token);
            if (changeModel)
                Assert.True(run.TrySetModelDuringRun("new-model", null, "new-provider", null, null, 0, "high", null,
                    () => agent.SetModelRuntime(client, supportsImages: true, imageResizeOptions: null)));
            else Assert.True(run.SetThinkingLevelDuringRun("high", null));
            client.Release.TrySetResult();
            await active;
            Assert.Equal($"{conversation.Id}|{sessionFile}|{(changeModel ? "new-provider|new-model" : "old-provider|old-model")}|high", client.BashResult);
            Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(root, "input")));
        }
        finally
        {
            client.Release.TrySetResult();
            deadline.Cancel();
            if (active is not null)
                try { await active; } catch (OperationCanceledException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SelectionClient : IChatClient
    {
        internal const string EnvironmentCommand = "printf '%s|%s|%s|%s|%s' \"${PI_SESSION_ID-unset}\" \"${PI_SESSION_FILE-unset}\" \"${PI_PROVIDER-unset}\" \"${PI_MODEL-unset}\" \"${PI_REASONING_LEVEL-unset}\"";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? BashResult { get; private set; }
        private int _requests;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requests);
            if (request == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("before-selection", "read", new Dictionary<string, object?> { ["path"] = "input" })]);
            }
            else if (request == 2)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("current-selection", "bash", new Dictionary<string, object?>
                    {
                        ["command"] = EnvironmentCommand
                    })]);
            else
            {
                BashResult = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
                    .Single(result => result.CallId == "current-selection").Result?.ToString();
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
