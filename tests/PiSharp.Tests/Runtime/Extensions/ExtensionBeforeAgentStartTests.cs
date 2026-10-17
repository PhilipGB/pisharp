using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class ExtensionBeforeAgentStartTests
{
    [Fact]
    public async Task BeforeAgentStartHandlersChainTurnPromptAndIgnoreHandlerFailure()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-before-agent-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var registration = new ExtensionRegistration();
            var order = new List<string>();
            PiSharpExtensionContext? observedContext = null;
            registration.AddBeforeAgentStartHandler((eventData, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal("hello", eventData.Prompt);
                Assert.Single(eventData.Images);
                Assert.Equal("image/png", eventData.Images[0].MediaType);
                Assert.Contains("base prompt", eventData.SystemPrompt, StringComparison.Ordinal);
                Assert.Contains("mcp context", eventData.SystemPrompt, StringComparison.Ordinal);
                Assert.Equal(workingDirectory, context.WorkingDirectory);
                Assert.Equal(conversation.Id, context.SessionId);
                Assert.False(context.IsIdle);
                observedContext = context;
                order.Add("first");
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(
                    new(eventData.SystemPrompt + "\nfirst"));
            });
            registration.AddBeforeAgentStartHandler((eventData, _, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.EndsWith("first", eventData.SystemPrompt, StringComparison.Ordinal);
                order.Add("second");
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(
                    new(eventData.SystemPrompt + "\nsecond"));
            });
            registration.AddBeforeAgentStartHandler((_, _, _) =>
                throw new InvalidOperationException("extension setup failed"));

            var provider = new BeforeAgentStartClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), systemPrompt: "base prompt",
                getAdditionalSystemInstructions: () => "mcp context",
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var events = new List<AgentLifecycleEvent>();

            var prompt = new ChatMessage(ChatRole.User,
                [new TextContent("hello"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")]);
            await foreach (var item in run.RunEventsAsync(prompt, "hello")) events.Add(item);

            Assert.NotNull(observedContext);
            Assert.True(observedContext.IsIdle);
            Assert.Equal(["first", "second"], order);
            var error = Assert.Single(events, item => item.Type == "extension_error");
            Assert.Equal("extension setup failed", error.Error);
            var instructions = Assert.Single(provider.Instructions);
            Assert.Contains("base prompt", instructions, StringComparison.Ordinal);
            Assert.Contains("first\nsecond", instructions, StringComparison.Ordinal);
            Assert.Equal(1, instructions.Split("mcp context", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("first", agent.SystemInstructions, StringComparison.Ordinal);
            Assert.Contains(run.Conversation.ContextMessages(), message =>
                message.Role == ChatRole.User && message.Text == "hello");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task IdleCustomMessageStartsOneCustomTurnAndPersistsReturnedMessageOnce()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var registration = new ExtensionRegistration();
            PiSharpBeforeAgentStartEvent? observed = null;
            registration.AddBeforeAgentStartHandler((eventData, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observed = eventData;
                Assert.Equal(PiSharpTurnMessageType.Custom, eventData.MessageType);
                Assert.Equal("", eventData.Prompt);
                Assert.NotNull(eventData.StartingCustomMessage);
                Assert.Equal("job-finished", eventData.StartingCustomMessage.CustomType);
                Assert.Equal("background work finished", eventData.StartingCustomMessage.Content);
                Assert.False(context.IsIdle);
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(
                    new() { InjectedMessage = new("extension-receipt", "result saved", Display: false) });
            });

            var provider = new BeforeAgentStartClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory),
                retryPolicy: ProviderRetryPolicy.None, beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunCustomMessageEventsAsync(
                new PiSharp.Extensions.Abstractions.PiSharpCustomMessage("job-finished", "background work finished", Display: true),
                "/extensions/background.cs"))
                events.Add(item);

            Assert.NotNull(observed);
            Assert.Single(provider.Requests);
            Assert.Equal(["background work finished", "result saved"], provider.Requests[0]
                .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
            var customEntries = conversation.Tree.ActivePath().Where(node => node.Type == "custom_message").ToArray();
            Assert.Equal(["job-finished", "extension-receipt"], customEntries
                .Select(node => node.Payload.GetProperty("piOriginalEntry").GetProperty("customType").GetString()));
            Assert.Equal("/extensions/background.cs", customEntries[0].Payload.GetProperty("source").GetString());
            Assert.False(customEntries[1].Payload.GetProperty("display").GetBoolean());
            Assert.Equal(2, customEntries.Length);
            Assert.Equal(["background work finished", "result saved", "done"],
                conversation.ContextMessages().Select(message => message.Text));
            Assert.Contains(events, item => item.Type == "turn_completed");
            Assert.False(run.IsActive);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task IdleCustomMessageWithoutTriggerIsPersistedForTheNextUserTurn()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-next-turn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var saved = 0;
            PiSharpBeforeAgentStartEvent? observed = null;
            var registration = new ExtensionRegistration();
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                observed = eventData;
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });
            var provider = new BeforeAgentStartClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), retryPolicy: ProviderRetryPolicy.None,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation,
                save: _ =>
                {
                    saved++;
                    return Task.CompletedTask;
                });

            Assert.True(await run.QueueExtensionMessageForNextTurnAsync(
                new PiSharp.Extensions.Abstractions.PiSharpCustomMessage("background-note", "ready for later"),
                "/extensions/example.cs"));
            Assert.Equal(1, saved);
            var customEntry = Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.Equal("/extensions/example.cs", customEntry.Payload.GetProperty("source").GetString());
            Assert.Equal(["ready for later"], conversation.ContextMessages().Select(message => message.Text));

            await foreach (var _ in run.RunEventsAsync("continue")) { }

            Assert.NotNull(observed);
            Assert.Equal(PiSharpTurnMessageType.User, observed.MessageType);
            Assert.Equal("continue", observed.Prompt);
            Assert.Equal(["ready for later", "continue"], provider.Requests[0]
                .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
            Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationWhilePersistingIdleCustomMessageRollsBackAndAllowsRecovery()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-persist-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saveInvocations = 0;
            var provider = new BeforeAgentStartClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), retryPolicy: ProviderRetryPolicy.None);
            var run = await ConversationRun.OpenAsync(agent, conversation, save: async cancellationToken =>
            {
                if (Interlocked.Increment(ref saveInvocations) == 1)
                {
                    saveStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            });
            using var cancellation = new CancellationTokenSource();
            var queue = run.QueueExtensionMessageForNextTurnAsync(
                new PiSharp.Extensions.Abstractions.PiSharpCustomMessage("cancelled-note", "do not retain"),
                "/extensions/example.cs", cancellation.Token);
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue);
            Assert.False(run.IsActive);
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.Empty(conversation.ContextMessages());

            await foreach (var _ in run.RunEventsAsync("recovered prompt")) { }
            Assert.Equal(["recovered prompt"], provider.Requests[0]
                .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ReturnedCustomMessageDoesNotDuplicateAcrossRetryOrNextUserTurn()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var registration = new ExtensionRegistration();
            var beforeStartCount = 0;
            registration.AddBeforeAgentStartHandler((_, _, _) =>
            {
                beforeStartCount++;
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(
                    new() { InjectedMessage = new("extension-note", "one durable note", Display: true) });
            });

            var provider = new RetryThenCompleteClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), retryPolicy: ProviderRetryPolicy.None,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation,
                retryPolicy: new AgentRunRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero),
                retryDelay: static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("hello")) events.Add(item);

            Assert.Equal(2, provider.Requests.Count);
            Assert.All(provider.Requests, request => Assert.Equal(1, request.Count(message =>
                message.Role == ChatRole.User && message.Text == "one durable note")));
            Assert.Equal(1, conversation.Tree.ActivePath().Count(node => node.Type == "custom_message"));
            Assert.Equal(1, beforeStartCount);
            Assert.Contains(events, item => item.Type == "turn_completed");
            Assert.False(run.IsActive);

            await foreach (var _ in run.RunEventsAsync("next")) { }
            Assert.Equal(2, conversation.Tree.ActivePath().Count(node => node.Type == "custom_message"));
            Assert.Equal(2, beforeStartCount);
            Assert.False(run.IsActive);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CustomMessageQueuedWhileBusyJoinsTheActiveRunWithoutAnotherStartEvent()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var registration = new ExtensionRegistration();
            var beforeStartCount = 0;
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                beforeStartCount++;
                Assert.Equal(PiSharpTurnMessageType.User, eventData.MessageType);
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });
            var provider = new HeldFirstRequestClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), retryPolicy: ProviderRetryPolicy.None,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var events = new List<AgentLifecycleEvent>();
            var execution = Task.Run(async () =>
            {
                await foreach (var item in run.RunEventsAsync("hello")) events.Add(item);
            });
            await provider.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(run.IsActive);
            Assert.False(await run.QueueExtensionMessageForNextTurnAsync(
                new PiSharp.Extensions.Abstractions.PiSharpCustomMessage("must-wait", "not idle"), "/extensions/example.cs"));
            Assert.True(run.TryQueueExtensionMessage(new("extension-update", "completed in background")));
            Assert.Equal(1, run.PendingPromptCount);
            provider.ReleaseFirstRequest.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, provider.Requests.Count);
            Assert.Contains(provider.Requests[1], message => message.Role == ChatRole.User &&
                message.Text == "completed in background");
            Assert.Equal(1, beforeStartCount);
            Assert.Equal(1, conversation.Tree.ActivePath().Count(node => node.Type == "custom_message"));
            Assert.Equal(0, run.PendingPromptCount);
            Assert.False(run.IsActive);
            Assert.Contains(events, item => item.Type == "agent_run_completed");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDuringCustomTurnSetupSettlesAndAllowsNextPrompt()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handlerInvocations = 0;
            var registration = new ExtensionRegistration();
            PiSharpExtensionContext? observedContext = null;
            registration.AddBeforeAgentStartHandler(async (_, context, cancellationToken) =>
            {
                observedContext = context;
                if (Interlocked.Increment(ref handlerInvocations) == 1)
                {
                    handlerStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return null;
            });
            var provider = new BeforeAgentStartClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), retryPolicy: ProviderRetryPolicy.None,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            using var cancellation = new CancellationTokenSource();
            var events = new List<AgentLifecycleEvent>();
            var execution = Task.Run(async () =>
            {
                await foreach (var item in run.RunCustomMessageEventsAsync(
                    new("cancelled-job", "do not persist before setup", Display: true), cancellation.Token))
                    events.Add(item);
            });
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(observedContext);
            Assert.False(observedContext.IsIdle);
            cancellation.Cancel();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains(events, item => item.Type == "prompt_rejected");
            Assert.False(run.IsActive);
            Assert.True(observedContext.IsIdle);
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "custom_message");

            await foreach (var _ in run.RunEventsAsync("next prompt")) { }
            Assert.Contains(conversation.ContextMessages(), message => message.Role == ChatRole.User &&
                message.Text == "next prompt");
            Assert.False(run.IsActive);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class BeforeAgentStartClient : IChatClient
    {
        public List<string> Instructions { get; } = [];
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            Instructions.Add(options?.Instructions ?? "");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RetryThenCompleteClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            if (Requests.Count == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, []);
                await Task.Yield();
                throw new HttpRequestException("503 service unavailable", null,
                    System.Net.HttpStatusCode.ServiceUnavailable);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class HeldFirstRequestClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Requests.Count;
            Requests.Add(messages.ToArray());
            if (request == 0)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
