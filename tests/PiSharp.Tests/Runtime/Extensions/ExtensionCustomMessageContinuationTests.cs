using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Tests;

public sealed class ExtensionCustomMessageContinuationTests
{
    [Fact]
    public async Task IdenticalContextOnlyMessagesRemainSeparateOccurrencesThroughContinuationAndLaterPrompt()
    {
        await using var fixture = new ContinuationFixture();
        var conversation = new ConversationSession(fixture.DirectoryPath, "fixture", null);
        var run = await fixture.OpenAsync(conversation);
        await fixture.SendAsync(run);
        var execution = fixture.RunAsync(run, "start tool work");
        await fixture.ToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await fixture.SendAsync(run);
        Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
        fixture.ReleaseTool.TrySetResult();
        await fixture.Provider.ContinuationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var continuation = fixture.Provider.Requests[1];
        var customIndexes = CustomIndexes(continuation);
        Assert.Equal(2, customIndexes.Length);
        var toolIndex = Array.FindIndex(continuation, message => message.Role == ChatRole.Tool);
        Assert.True(customIndexes[0] < toolIndex);
        Assert.True(customIndexes[1] > toolIndex);
        var entries = conversation.Tree.ActivePath().Where(node => node.Type == "custom_message").ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(entries.Select(entry => entry.Id), customIndexes.Select(index => continuation[index].MessageId));

        fixture.Provider.ReleaseContinuation.TrySetResult();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, fixture.Provider.Requests.Count);
        await fixture.RunAsync(run, "what finished?");
        Assert.Equal(3, fixture.Provider.Requests.Count);
        Assert.Equal(2, CustomIndexes(fixture.Provider.Requests[2]).Length);
        Assert.Equal(2, conversation.Tree.ActivePath().Count(node => node.Type == "custom_message"));
    }

    [Fact]
    public async Task ContextOnlyMessageIsSavedBeforeContinuationAndRecoveredBeforeRunFinishes()
    {
        await using var fixture = new ContinuationFixture();
        var conversation = new ConversationSession(fixture.DirectoryPath, "fixture", null);
        var store = new ConversationStore(fixture.DirectoryPath, Path.Combine(fixture.DirectoryPath, "sessions"));
        var path = store.NewPath(conversation);
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var customSaved = false;
        async Task SaveAsync(CancellationToken token)
        {
            var savingCustom = !customSaved && conversation.Tree.ActivePath().Any(node => node.Type == "custom_message");
            if (savingCustom)
            {
                saveStarted.TrySetResult();
                await releaseSave.Task.WaitAsync(fixture.Token);
            }
            await store.SaveAsync(conversation, path, token);
            if (savingCustom) customSaved = true;
        }

        var run = await fixture.OpenAsync(conversation, SaveAsync);
        var execution = fixture.RunAsync(run, "start tool work");
        try
        {
            await fixture.ToolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.SendAsync(run);
            fixture.ReleaseTool.TrySetResult();
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(fixture.Provider.ContinuationStarted.Task.IsCompleted);
            Assert.Single(fixture.Provider.Requests);
            var checkpoint = await new ConversationStore(fixture.DirectoryPath).LoadAsync(path);
            Assert.Contains(checkpoint.Tree.ActivePath(), node => node.Type == "tool_outcome");
            Assert.DoesNotContain(checkpoint.Tree.ActivePath(), node => node.Type == "custom_message");
            releaseSave.TrySetResult();
            await fixture.Provider.ContinuationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(customSaved);

            var recovered = await new ConversationStore(fixture.DirectoryPath).LoadAsync(path);
            Assert.DoesNotContain(recovered.Tree.ActivePath(), node => node.Type == "run_finished");
            var customEntry = Assert.Single(recovered.Tree.ActivePath(), node => node.Type == "custom_message");
            var savedContext = recovered.ContextMessages().ToArray();
            var toolIndex = Array.FindIndex(savedContext, message => message.Role == ChatRole.Tool);
            Assert.True(toolIndex >= 0);
            Assert.True(Assert.Single(CustomIndexes(savedContext)) > toolIndex);
            Assert.Equal(customEntry.Id, savedContext[CustomIndexes(savedContext)[0]].MessageId);
            Assert.Single(CustomIndexes(fixture.Provider.Requests[1]));

            var resumedProvider = new HeldContinuationClient(requestTool: false);
            var resumed = await ConversationRun.OpenAsync(new PiAgent(resumedProvider,
                new CodingTools(fixture.DirectoryPath), noBuiltinTools: true), recovered);
            await fixture.RunAsync(resumed, "continue after crash");
            Assert.Single(CustomIndexes(Assert.Single(resumedProvider.Requests)));
            Assert.Single(recovered.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.Equal(1, fixture.ToolExecutions);

            fixture.Provider.ReleaseContinuation.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, fixture.Provider.Requests.Count);
        }
        finally
        {
            releaseSave.TrySetResult();
        }
    }

    private static int[] CustomIndexes(ChatMessage[] messages) => messages.Select((message, index) => (message, index))
        .Where(entry => PiSharpCustomMessageProjection.TryRead(entry.message, out var custom) &&
            custom.CustomType == "status" && custom.Content == "finished" && custom.Source == "/extensions/background.cs")
        .Select(entry => entry.index).ToArray();

    private sealed class ContinuationFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new(TimeSpan.FromSeconds(20));
        private readonly List<Task> _runs = [];
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "pisharp-custom-continuation-" + Guid.NewGuid().ToString("N"));
        public TaskCompletionSource ToolStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTool { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HeldContinuationClient Provider { get; } = new();
        public CancellationToken Token => _cancel.Token;
        public int ToolExecutions { get; private set; }

        public ContinuationFixture() => Directory.CreateDirectory(DirectoryPath);

        public Task<ConversationRun> OpenAsync(ConversationSession conversation, Func<CancellationToken, Task>? save = null)
        {
            var hold = AIFunctionFactory.Create(async (CancellationToken token) =>
            {
                ToolExecutions++;
                ToolStarted.TrySetResult();
                await ReleaseTool.Task.WaitAsync(token);
                return "tool result";
            }, name: "hold");
            return ConversationRun.OpenAsync(new PiAgent(Provider, new CodingTools(DirectoryPath), noBuiltinTools: true,
                extensionToolRegistrations: [new PiSharpToolRegistration(hold, ToolExposure.ModelOnly)]), conversation, save: save);
        }

        public async Task SendAsync(ConversationRun run) => Assert.False(await run.DeliverExtensionCustomMessageAsync(
            new NativeExtensions.PiSharpCustomMessage("status", "finished"), "/extensions/background.cs", triggerTurn: false,
            deliveryMode: NativeExtensions.PiSharpMessageDeliveryMode.FollowUp));

        public Task RunAsync(ConversationRun run, string prompt)
        {
            var task = Task.Run(async () =>
            {
                await foreach (var _ in run.RunEventsAsync(prompt, Token)) { }
            });
            _runs.Add(task);
            return task;
        }

        public async ValueTask DisposeAsync()
        {
            _cancel.Cancel();
            ReleaseTool.TrySetResult();
            Provider.ReleaseContinuation.TrySetResult();
            try { await Task.WhenAll(_runs); }
            catch (OperationCanceledException) { }
            _cancel.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class HeldContinuationClient(bool requestTool = true) : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public TaskCompletionSource ContinuationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseContinuation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var requestIndex = Requests.Count;
            Requests.Add(messages.ToArray());
            if (requestTool && requestIndex == 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("hold-call", "hold", new Dictionary<string, object?>())]);
            else
            {
                if (requestTool && requestIndex == 1)
                {
                    ContinuationStarted.TrySetResult();
                    await ReleaseContinuation.Task.WaitAsync(cancellationToken);
                }
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
