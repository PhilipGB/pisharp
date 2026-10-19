using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionMessageLifecycleTests
{
    [Fact]
    public async Task PromptAndAssistantLifecycleIsOrderedAndAcceptedReplacementsReachHistory()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var order = new List<string>();
            string? settledAssistantText = null;
            string? turnEndAssistantText = null;
            string? providerPrompt = null;
            adapter.AddAgentStartHandler((_, _, _) => { order.Add("agent_start"); return ValueTask.CompletedTask; });
            adapter.AddTurnStartHandler((_, _, _) => { order.Add("turn_start"); return ValueTask.CompletedTask; });
            adapter.AddMessageStartHandler((eventData, _, _) =>
            {
                order.Add($"message_start:{eventData.Message.Role}");
                return ValueTask.CompletedTask;
            });
            adapter.AddMessageUpdateHandler((eventData, _, _) =>
            {
                order.Add($"message_update:{eventData.UpdateType}");
                Assert.Equal(PiSharpExtensionMessageRole.Assistant, eventData.Message.Role);
                return ValueTask.CompletedTask;
            });
            adapter.AddMessageEndHandler((eventData, _, _) =>
            {
                order.Add($"message_end:{eventData.Message.Role}");
                var text = eventData.Message.Contents.Single().Text;
                var replacement = eventData.Message.Role switch
                {
                    PiSharpExtensionMessageRole.User => new PiSharpExtensionMessage(eventData.Message.Role,
                        [new(PiSharpExtensionMessageContentKind.Text, "accepted prompt")]),
                    PiSharpExtensionMessageRole.Assistant => new PiSharpExtensionMessage(eventData.Message.Role,
                        [new(PiSharpExtensionMessageContentKind.Text, "accepted answer")],
                        messageId: eventData.Message.MessageId, authorName: eventData.Message.AuthorName,
                        createdAt: eventData.Message.CreatedAt,
                        additionalProperties: eventData.Message.AdditionalProperties),
                    _ => null
                };
                return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(replacement is null ? null : new(replacement));
            });
            adapter.AddAgentEndHandler((eventData, _, _) =>
            {
                settledAssistantText = eventData.Messages.LastOrDefault(message => message.Role == ChatRole.Assistant)?.Text;
                return ValueTask.CompletedTask;
            });
            adapter.AddTurnEndHandler((eventData, _, _) =>
            {
                if (eventData.Message?.Role == ChatRole.Assistant) turnEndAssistantText = eventData.Message.Text;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var client = new TextResponseClient(messages =>
                providerPrompt = messages.Last(message => message.Role == ChatRole.User).Text);
            var agent = new PiAgent(client, new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation, save: _ => Task.CompletedTask);
            await foreach (var _ in run.RunEventsAsync("original prompt")) { }

            Assert.Equal("accepted prompt", providerPrompt);
            Assert.Equal(["agent_start", "turn_start", "message_start:User", "message_end:User",
                "message_start:Assistant", "message_update:text_delta", "message_update:text_delta",
                "message_end:Assistant"], order);
            Assert.Contains(conversation.ContextMessages(), message =>
                message.Role == ChatRole.User && message.Text == "accepted prompt");
            Assert.Contains(conversation.ContextMessages(), message =>
                message.Role == ChatRole.Assistant && message.Text == "accepted answer");
            Assert.Equal("accepted answer", settledAssistantText);
            Assert.Equal("accepted answer", turnEndAssistantText);
            Assert.Single(conversation.ContextMessages(), message =>
                message.Role == ChatRole.Assistant && message.Text == "accepted answer");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ToolResultReplacementIsUsedByTheFollowingProviderRequest()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var startedRoles = new List<PiSharpExtensionMessageRole>();
            var observedUpdates = new List<PiSharpExtensionMessageUpdateEvent>();
            var endedRoles = new List<PiSharpExtensionMessageRole>();
            adapter.AddMessageStartHandler((eventData, _, _) =>
            {
                startedRoles.Add(eventData.Message.Role);
                return ValueTask.CompletedTask;
            });
            adapter.AddMessageUpdateHandler((eventData, _, _) =>
            {
                observedUpdates.Add(eventData);
                return ValueTask.CompletedTask;
            });
            adapter.AddMessageEndHandler((eventData, _, _) =>
            {
                endedRoles.Add(eventData.Message.Role);
                return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(null);
            });
            var toolRegistration = new PiSharpToolRegistration(AIFunctionFactory.Create(
                (string value) => "raw tool result", name: "echo"), ToolExposure.ModelOnly);
            adapter.AddMessageEndHandler((eventData, _, _) =>
            {
                if (eventData.Message.Role != PiSharpExtensionMessageRole.ToolResult)
                    return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(null);
                var result = eventData.Message.Contents.OfType<PiSharpExtensionMessageContent>()
                    .Single(content => content.Kind == PiSharpExtensionMessageContentKind.FunctionResult);
                return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(new(new PiSharpExtensionMessage(
                    eventData.Message.Role,
                    [new(result.CallId!, JsonSerializer.SerializeToElement("accepted tool result"))])));
            });
            adapter.Close();

            var client = new ToolCallClient();
            var agent = new PiAgent(client, new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration, extensionToolRegistrations: [toolRegistration]);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation, save: _ => Task.CompletedTask);
            await foreach (var _ in run.RunEventsAsync("run tool")) { }

            var followUpRequest = Assert.Single(client.Requests.Skip(1));
            var functionResult = Assert.Single(followUpRequest.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>());
            Assert.Equal("accepted tool result", Assert.IsType<JsonElement>(functionResult.Result).GetString());
            Assert.Contains(conversation.ContextMessages().SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>(), result => result.Result?.ToString() == "accepted tool result");
            Assert.Contains(PiSharpExtensionMessageRole.User, startedRoles);
            Assert.Contains(PiSharpExtensionMessageRole.Assistant, startedRoles);
            Assert.Contains(PiSharpExtensionMessageRole.ToolResult, startedRoles);
            Assert.Contains(PiSharpExtensionMessageRole.ToolResult, endedRoles);
            var toolCallUpdate = Assert.Single(observedUpdates, update => update.UpdateType == "toolcall_delta");
            Assert.Equal(PiSharpExtensionMessageContentKind.FunctionCall, toolCallUpdate.Content!.Kind);
            Assert.Equal("call-1", toolCallUpdate.Content.CallId);
            Assert.Equal("echo", toolCallUpdate.Content.FunctionName);
            Assert.Equal(JsonValueKind.Object, toolCallUpdate.Content.JsonValue!.Value.ValueKind);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EndHandlersComposeAndRejectRoleChangesWithoutStoppingLaterHandlers()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var first = Source("/extensions/first.dll");
        var malformed = Source("/extensions/malformed.dll");
        var broken = Source("/extensions/broken.dll");
        var last = Source("/extensions/last.dll");
        var observed = new List<string>();

        registration.SetCurrentSourceInfo(first);
        adapter.AddMessageEndHandler((_, _, _) => ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(new(
            new PiSharpExtensionMessage(PiSharpExtensionMessageRole.Assistant,
                [new(PiSharpExtensionMessageContentKind.Text, "wrong role")]))));
        registration.SetCurrentSourceInfo(malformed);
        adapter.AddMessageEndHandler((_, _, _) => ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(new(
            new PiSharpExtensionMessage(PiSharpExtensionMessageRole.User,
                [new("call-malformed", "echo", JsonSerializer.SerializeToElement("arguments must be an object"))]))));
        registration.SetCurrentSourceInfo(broken);
        adapter.AddMessageEndHandler((_, _, _) => ValueTask.FromException<PiSharpExtensionMessageEndResult?>(
            new InvalidOperationException("message end failed")));
        registration.SetCurrentSourceInfo(last);
        adapter.AddMessageEndHandler((eventData, _, _) =>
        {
            observed.Add(eventData.Message.Contents.Single().Text!);
            return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(new(
                new PiSharpExtensionMessage(eventData.Message.Role,
                    [new(PiSharpExtensionMessageContentKind.Text, "last accepted")])));
        });
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var result = await dispatcher.DispatchMessageEndAsync(new(PiSharpExtensionMessageRole.User,
            [new(PiSharpExtensionMessageContentKind.Text, "original")]), CancellationToken.None);

        Assert.Equal(["original"], observed);
        Assert.Equal("last accepted", result.Contents.Single().Text);
        Assert.Collection(diagnostics,
            item => Assert.Equal(first.Path, ExtensionPath(item)),
            item => Assert.Equal(malformed.Path, ExtensionPath(item)),
            item =>
            {
                Assert.Equal(broken.Path, ExtensionPath(item));
                Assert.Equal("message end failed", item.Error);
            });
    }

    private static string CreateWorkingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-native-message-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? ExtensionPath(AgentLifecycleEvent diagnostic) =>
        diagnostic.Details?.GetType().GetProperty("extensionPath")?.GetValue(diagnostic.Details) as string;

    private static ResourceSourceInfo Source(string path) => new(path, "native-package", "user", "package", null);

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateContext() => new(
        new PiSharpExtensionContextConfiguration("/tmp", "test", HasUserInterface: false,
            IsProjectTrusted: true, new TestUi()), static () => null, static () => [], static () => true);

    private sealed class TestUi : PiSharp.Runtime.Extensions.IPiSharpExtensionUi
    {
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public void Notify(string message, string type = "info") { }
    }

    private sealed class TextResponseClient(Action<IReadOnlyList<ChatMessage>>? inspect = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = messages.ToArray();
            inspect?.Invoke(request);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial ");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "answer");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ToolCallClient : IChatClient
    {
        private int _requests;
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            if (Interlocked.Increment(ref _requests) == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "echo", new Dictionary<string, object?> { ["value"] = "input" })]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task IdleCustomMessageLifecyclePersistsItsAcceptedReplacement()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<string>();
            adapter.AddMessageStartHandler((eventData, _, _) =>
            {
                observed.Add($"start:{eventData.Message.Role}");
                return ValueTask.CompletedTask;
            });
            adapter.AddMessageEndHandler((eventData, _, _) =>
            {
                observed.Add($"end:{eventData.Message.Role}");
                var properties = eventData.Message.AdditionalProperties;
                return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(new(new(
                    PiSharpExtensionMessageRole.Custom,
                    [new(PiSharpExtensionMessageContentKind.Text, "accepted custom")],
                    additionalProperties: properties)));
            });
            adapter.Close();

            var agent = new PiAgent(new TextResponseClient(), new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation, save: _ => Task.CompletedTask);
            var delivered = await run.DeliverExtensionCustomMessageAsync(new("fixture", "original custom"),
                "/extensions/custom.dll", triggerTurn: false, PiSharpMessageDeliveryMode.Steer);

            Assert.False(delivered);
            Assert.Equal(["start:Custom", "end:Custom"], observed);
            Assert.Single(conversation.ContextMessages(), message => message.Text == "accepted custom" &&
                PiSharpCustomMessageProjection.TryRead(message, out var custom) && custom.CustomType == "fixture");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task QueuedFollowUpReplacementIsSentInTheSubsequentProviderRequest()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            adapter.AddMessageEndHandler((eventData, _, _) =>
            {
                if (eventData.Message.Role != PiSharpExtensionMessageRole.User)
                    return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(null);
                return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(new(new(
                    PiSharpExtensionMessageRole.User,
                    [new(PiSharpExtensionMessageContentKind.Text, "accepted " + eventData.Message.Contents.Single().Text)])));
            });
            adapter.Close();

            ConversationRun? run = null;
            var providerUsers = new List<string?>();
            var client = new TextResponseClient(messages =>
            {
                providerUsers.Add(messages.Last(message => message.Role == ChatRole.User).Text);
                if (providerUsers.Count == 1) Assert.True(run!.TryFollowUp("queued follow up"));
            });
            var agent = new PiAgent(client, new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            run = await ConversationRun.OpenAsync(agent, conversation, save: _ => Task.CompletedTask);
            await foreach (var _ in run.RunEventsAsync("first prompt")) { }

            Assert.Equal(["accepted first prompt", "accepted queued follow up"], providerUsers);
            Assert.Single(conversation.ContextMessages(), message =>
                message.Role == ChatRole.User && message.Text == "accepted queued follow up");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProviderFailureAfterPartialAssistantUpdateDoesNotEmitMessageEnd()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var events = new List<string>();
            adapter.AddMessageStartHandler((eventData, _, _) =>
            {
                events.Add($"start:{eventData.Message.Role}");
                return ValueTask.CompletedTask;
            });
            adapter.AddMessageUpdateHandler((eventData, _, _) =>
            {
                events.Add($"update:{eventData.UpdateType}");
                return ValueTask.CompletedTask;
            });
            adapter.AddMessageEndHandler((eventData, _, _) =>
            {
                events.Add($"end:{eventData.Message.Role}");
                return ValueTask.FromResult<PiSharpExtensionMessageEndResult?>(null);
            });
            adapter.Close();

            var agent = new PiAgent(new PartialFailureClient(), new CodingTools(workingDirectory), noBuiltinTools: true,
                liveExtensionRegistration: registration);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation, save: _ => Task.CompletedTask);
            var runEvents = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("fail after partial response")) runEvents.Add(item);

            Assert.Equal(["start:User", "end:User", "start:Assistant", "update:text_delta"], events);
            Assert.Contains(runEvents, item => item.Type == "turn_failed");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDetachesMessageHandlerAndRevokesItsPublicContext()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var started = new TaskCompletionSource<(PiSharp.Extensions.Abstractions.PiSharpExtensionContext Context,
            CancellationToken Token)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.AddMessageStartHandler(async (_, context, token) =>
        {
            started.TrySetResult((context, token));
            try { await release.Task; }
            finally { handlerFinished.TrySetResult(); }
        });
        adapter.Close();

        var runtimeContext = CreateContext();
        runtimeContext.SetSessionContextFactory(_ => new PiSharpExtensionSessionContext(
            () => new("session", null, null, []),
            (_, _, _) => throw new InvalidOperationException("A revoked session context must not write.")));
        runtimeContext.SetCapabilitiesFactory((_, session) => new PiSharpExtensionCapabilities(null, session, null));
        var dispatcher = new NativeExtensionEventDispatcher(registration, runtimeContext, _ => { });
        using var cancellation = new CancellationTokenSource();
        var dispatch = dispatcher.DispatchMessageStartAsync(new(new(PiSharpExtensionMessageRole.User,
            [new(PiSharpExtensionMessageContentKind.Text, "prompt")])), cancellation.Token);
        var callback = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var session = callback.Context.Session!;
        var capabilities = callback.Context.Capabilities!;

        try
        {
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(callback.Token.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => { _ = session.Snapshot; });
            Assert.Throws<ObjectDisposedException>(() => { _ = capabilities.GetSessionSnapshot(); });
            Assert.False(handlerFinished.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await handlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class PartialFailureClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "partial only");
            await Task.Yield();
            throw new InvalidOperationException("provider stream failed");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
