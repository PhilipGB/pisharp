using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionHookBridgeTests
{
    [Fact]
    public async Task PublicBeforeAgentStartContextCapturesCurrentTrustIdleAndPendingState()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            NativeExtensions.PiSharpExtensionContext? observedContext = null;
            NativeExtensions.PiSharpBeforeAgentStartEvent? observedEvent = null;
            adapter.AddBeforeAgentStartHandler((turn, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedEvent = turn;
                observedContext = context;
                return ValueTask.FromResult<NativeExtensions.PiSharpBeforeAgentStartResult?>(null);
            });
            adapter.Close();

            var agent = new PiAgent(new HookBridgeClient(), new CodingTools(cwd), noBuiltinTools: true,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers,
                extensionContextConfiguration: new PiSharpExtensionContextConfiguration(
                    cwd, "print", false, true, new TestExtensionUi()));
            var run = await ConversationRun.OpenAsync(agent, new ConversationSession(cwd, "test-model", null));

            var image = new DataContent(new byte[] { 1, 2, 3 }, "image/png");
            var prompt = new ChatMessage(ChatRole.User, [new TextContent("context check"), image]);
            await foreach (var _ in run.RunEventsAsync(prompt, "context check")) { }

            Assert.NotNull(observedEvent);
            Assert.Single(observedEvent.Images);
            Assert.Equal("image/png", observedEvent.Images[0].MediaType);
            Assert.Equal(3, observedEvent.Images[0].ByteLength);
            Assert.NotNull(observedContext);
            Assert.Equal(cwd, observedContext.WorkingDirectory);
            Assert.False(observedContext.IsIdle);
            Assert.True(observedContext.IsProjectTrusted);
            Assert.False(observedContext.HasPendingMessages);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PublicBeforeAgentStartContextCapturesPendingMessagesAddedDuringSetup()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            NativeExtensions.PiSharpExtensionContext? firstContext = null;
            NativeExtensions.PiSharpExtensionContext? secondContext = null;
            ConversationRun? run = null;
            var queued = false;
            adapter.AddBeforeAgentStartHandler((_, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                firstContext = context;
                queued = run!.TryQueueExtensionMessage(new PiSharpCustomMessage("setup-update", "queued during setup"));
                return ValueTask.FromResult<NativeExtensions.PiSharpBeforeAgentStartResult?>(null);
            });
            adapter.AddBeforeAgentStartHandler((_, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                secondContext = context;
                return ValueTask.FromResult<NativeExtensions.PiSharpBeforeAgentStartResult?>(null);
            });
            adapter.Close();

            var provider = new HookBridgeClient();
            run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                    beforeAgentStartHandlers: registration.BeforeAgentStartHandlers,
                    extensionContextConfiguration: new PiSharpExtensionContextConfiguration(
                        cwd, "print", false, true, new TestExtensionUi())),
                new ConversationSession(cwd, "test-model", null));

            await foreach (var _ in run.RunEventsAsync("setup prompt")) { }

            Assert.True(queued);
            Assert.NotNull(firstContext);
            Assert.False(firstContext.HasPendingMessages);
            Assert.False(firstContext.IsIdle);
            Assert.NotNull(secondContext);
            Assert.True(secondContext.HasPendingMessages);
            Assert.False(secondContext.IsIdle);
            var request = Assert.Single(provider.Requests);
            Assert.Contains(request, message => message.Text == "setup prompt");
            Assert.Contains(request, message => message.Text == "queued during setup");
            Assert.Equal(0, run.PendingPromptCount);
            Assert.False(run.IsActive);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Theory]
    [InlineData(NativeExtensions.PiSharpMessageDeliveryMode.NextTurn, true)]
    [InlineData(NativeExtensions.PiSharpMessageDeliveryMode.NextTurn, false)]
    [InlineData(NativeExtensions.PiSharpMessageDeliveryMode.Steer, false)]
    [InlineData(NativeExtensions.PiSharpMessageDeliveryMode.FollowUp, false)]
    public async Task PublicContextReportsHeldMessagesWithoutChangingTheOrdinaryPromptQueue(
        NativeExtensions.PiSharpMessageDeliveryMode deliveryMode, bool triggerTurn)
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            ConversationRun? run = null;
            var send = true;
            var pendingSnapshots = new List<bool>();
            adapter.AddBeforeAgentStartHandler(async (_, _, cancellationToken) =>
            {
                if (send)
                {
                    send = false;
                    Assert.False(await run!.DeliverExtensionCustomMessageAsync(
                        new("held", "held message"), null, triggerTurn, deliveryMode, cancellationToken));
                }
                return null;
            });
            adapter.AddBeforeAgentStartHandler((_, context, _) =>
            {
                pendingSnapshots.Add(context.HasPendingMessages);
                Assert.Equal(0, run!.PendingPromptCount);
                Assert.Empty(run.GetPendingPrompts().Steering);
                Assert.Empty(run.GetPendingPrompts().FollowUp);
                return ValueTask.FromResult<NativeExtensions.PiSharpBeforeAgentStartResult?>(null);
            });
            adapter.Close();

            var provider = new HookBridgeClient();
            run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers),
                new ConversationSession(cwd, "test-model", null));

            await foreach (var _ in run.RunEventsAsync("first prompt")) { }

            Assert.Equal([true], pendingSnapshots);
            var nextTurn = deliveryMode == NativeExtensions.PiSharpMessageDeliveryMode.NextTurn;
            Assert.Equal(nextTurn, run.HasPendingExtensionMessages);
            Assert.Equal(0, run.PendingPromptCount);
            var firstRequest = Assert.Single(provider.Requests);
            Assert.Equal(!nextTurn, firstRequest.Any(message => message.Text == "held message"));

            await foreach (var _ in run.RunEventsAsync("next prompt")) { }

            Assert.Equal([true, false], pendingSnapshots);
            Assert.False(run.HasPendingExtensionMessages);
            Assert.Equal(2, provider.Requests.Count);
            Assert.Single(provider.Requests[1], message => message.Text == "held message");
            Assert.Single(run.Conversation.Tree.ActivePath(), node => node.Type == "custom_message");
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PublicContextTransformFailureSettlesAndAHealthyFollowupSucceeds()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var calls = 0;
            adapter.AddContextTransform((messages, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref calls) == 1)
                    return ValueTask.FromException<IReadOnlyList<ChatMessage>>(
                        new InvalidOperationException("public transform failed"));
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(
                    messages.Select(message => message.Clone()).ToArray());
            });
            adapter.Close();

            var provider = new HookBridgeClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                    extensionContextTransforms: registration.ContextTransforms),
                new ConversationSession(cwd, "test-model", null));
            var failedEvents = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("fails before provider")) failedEvents.Add(item);

            Assert.Contains(failedEvents, item => item.Type == "turn_failed" &&
                item.Error == "public transform failed");
            Assert.Empty(provider.Requests);
            Assert.False(run.IsActive);

            var recoveryEvents = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("healthy followup")) recoveryEvents.Add(item);

            Assert.Contains(recoveryEvents, item => item.Type == "agent_run_completed");
            Assert.Single(provider.Requests);
            Assert.Equal(2, calls);
            Assert.False(run.IsActive);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PublicContextTransformsComposePerRequestWithoutChangingConversationHistory()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var order = new List<string>();
            adapter.AddContextTransform((messages, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("first");
                var transformed = messages.Select(message => message.Clone()).ToArray();
                AppendToLastUserMessage(transformed, " [first]");
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(transformed);
            });
            adapter.AddContextTransform((messages, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("second");
                Assert.Contains("[first]", messages.Last(message => message.Role == ChatRole.User).Text);
                var transformed = messages.Select(message => message.Clone()).ToArray();
                AppendToLastUserMessage(transformed, " [second]");
                return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(transformed);
            });
            adapter.Close();

            var provider = new HookBridgeClient();
            var conversation = new ConversationSession(cwd, "test-model", null);
            conversation.Append(new ChatMessage(ChatRole.User, "older context"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "older answer"));
            var run = await ConversationRun.OpenAsync(
                new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                    extensionContextTransforms: registration.ContextTransforms),
                conversation);

            await foreach (var _ in run.RunEventsAsync("prompt")) { }

            Assert.Equal(["first", "second"], order);
            var request = Assert.Single(provider.Requests);
            var providerPrompt = request.Last(message => message.Role == ChatRole.User);
            Assert.Contains("[first]", providerPrompt.Text);
            Assert.Contains("[second]", providerPrompt.Text);
            Assert.Contains(run.Conversation.ContextMessages(), message =>
                message.Role == ChatRole.User && message.Text == "prompt");
            Assert.DoesNotContain(run.Conversation.ContextMessages(), message =>
                message.Text.Contains("[first]", StringComparison.Ordinal) ||
                message.Text.Contains("[second]", StringComparison.Ordinal));

            var compaction = Assert.IsType<ConversationSession.CompactionPlan>(run.Conversation.PrepareCompaction());
            run.Conversation.AppendCompaction(compaction, "older context summary");
            var compactedOrder = order.Count;
            await foreach (var _ in run.RunEventsAsync("after compaction")) { }

            Assert.Equal(["first", "second"], order.Skip(compactedOrder));
            var compactedPrompt = provider.Requests[^1].Last(message => message.Role == ChatRole.User);
            Assert.Contains("after compaction", compactedPrompt.Text, StringComparison.Ordinal);
            Assert.Contains("[first]", compactedPrompt.Text, StringComparison.Ordinal);
            Assert.Contains("[second]", compactedPrompt.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(run.Conversation.ContextMessages(), message =>
                message.Text.Contains("[first]", StringComparison.Ordinal) ||
                message.Text.Contains("[second]", StringComparison.Ordinal));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PublicToolHooksMutateArgumentsAndComposeResultTransformationsInOrder()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var order = new List<string>();
            string? resultSeenByFirstHook = null;
            string? resultSeenBySecondHook = null;
            var resultWasError = true;
            adapter.AddToolCallHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("call-first");
                Assert.Equal("call-id", context.ToolCallId);
                Assert.Equal("echo", context.ToolName);
                context.Arguments["value"] = "first";
                return ValueTask.FromResult(NativeExtensions.PiSharpToolCallDecision.Allow);
            });
            adapter.AddToolCallHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("call-second");
                Assert.Equal("first", context.Arguments["value"]);
                context.Arguments["value"] = "second";
                return ValueTask.FromResult(NativeExtensions.PiSharpToolCallDecision.Allow);
            });
            adapter.AddToolResultHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("result-first");
                resultWasError = context.IsError;
                resultSeenByFirstHook = context.Result?.ToString();
                context.Result = "first:" + context.Result;
                return ValueTask.CompletedTask;
            });
            adapter.AddToolResultHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                order.Add("result-second");
                resultSeenBySecondHook = context.Result?.ToString();
                context.Result = "second:" + context.Result;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var tool = AIFunctionFactory.Create((string value) => "echo:" + value, name: "echo");
            var provider = new HookBridgeClient("echo", "call-id");
            var agent = new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                extensionToolRegistrations: [new(tool, ToolExposure.ModelOnly)],
                extensionToolCallHooks: registration.ToolCallHooks,
                extensionToolResultHooks: registration.ToolResultHooks);
            var run = await ConversationRun.OpenAsync(agent, new ConversationSession(cwd, "test-model", null));

            await foreach (var _ in run.RunEventsAsync("call echo")) { }

            Assert.Equal(["call-first", "call-second", "result-first", "result-second"], order);
            Assert.False(resultWasError);
            Assert.Equal("echo:second", resultSeenByFirstHook);
            Assert.Equal("first:echo:second", resultSeenBySecondHook);
            Assert.Equal("second:first:echo:second", provider.ToolResult);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task PublicToolCallHookCanBlockExecutionAndResultHookSeesTheBlockedError()
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var invocations = 0;
            string? blockedError = null;
            adapter.AddToolCallHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal("blocked-call", context.ToolCallId);
                return ValueTask.FromResult(NativeExtensions.PiSharpToolCallDecision.Block("policy blocked"));
            });
            adapter.AddToolResultHook((context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.True(context.IsError);
                blockedError = context.Error;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var tool = AIFunctionFactory.Create(() =>
            {
                invocations++;
                return "should not run";
            }, name: "protected");
            var provider = new HookBridgeClient("protected", "blocked-call");
            var agent = new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                extensionToolRegistrations: [new(tool, ToolExposure.ModelOnly)],
                extensionToolCallHooks: registration.ToolCallHooks,
                extensionToolResultHooks: registration.ToolResultHooks);
            var run = await ConversationRun.OpenAsync(agent, new ConversationSession(cwd, "test-model", null));
            var events = new List<AgentLifecycleEvent>();

            await foreach (var item in run.RunEventsAsync("invoke protected")) events.Add(item);

            Assert.Equal(0, invocations);
            Assert.Equal("policy blocked", blockedError);
            Assert.Contains(events, item => item.Type == "tool_execution_finished" && item.IsError == true &&
                item.Error == "policy blocked");
            Assert.Equal(2, provider.Requests.Count);
            Assert.False(string.IsNullOrWhiteSpace(provider.ToolResult));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Theory]
    [InlineData("call")]
    [InlineData("result")]
    public async Task PublicToolHookFailureIsReportedAndTheNextPromptSucceeds(string failurePhase)
    {
        var cwd = NewWorkingDirectory();
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            adapter.AddToolCallHook((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failurePhase == "call") throw new InvalidOperationException("public call hook failed");
                return ValueTask.FromResult(NativeExtensions.PiSharpToolCallDecision.Allow);
            });
            adapter.AddToolResultHook((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failurePhase == "result") throw new InvalidOperationException("public result hook failed");
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var invocations = 0;
            var tool = AIFunctionFactory.Create((string unused) =>
            {
                invocations++;
                return "protected result";
            }, name: "protected");
            var provider = new HookBridgeClient("protected", "hook-failure-call");
            var agent = new PiAgent(provider, new CodingTools(cwd), noBuiltinTools: true,
                extensionToolRegistrations: [new(tool, ToolExposure.ModelOnly)],
                extensionToolCallHooks: registration.ToolCallHooks,
                extensionToolResultHooks: registration.ToolResultHooks);
            var run = await ConversationRun.OpenAsync(agent, new ConversationSession(cwd, "test-model", null));
            var failedHookEvents = new List<AgentLifecycleEvent>();

            await foreach (var item in run.RunEventsAsync("exercise public hook")) failedHookEvents.Add(item);

            Assert.Contains(failedHookEvents, item => item.Type == "tool_execution_finished" && item.IsError == true &&
                item.Error!.Contains($"public {failurePhase} hook failed", StringComparison.Ordinal));
            Assert.Equal(failurePhase == "call" ? 0 : 1, invocations);
            Assert.Equal(2, provider.Requests.Count);
            Assert.False(run.IsActive);

            var recoveryEvents = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("ordinary recovery")) recoveryEvents.Add(item);

            Assert.Contains(recoveryEvents, item => item.Type == "agent_run_completed");
            Assert.Equal(3, provider.Requests.Count);
            Assert.False(run.IsActive);
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    private static string NewWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-public-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        return cwd;
    }

    private static void AppendToLastUserMessage(IReadOnlyList<ChatMessage> messages, string suffix)
    {
        var message = messages.Last(item => item.Role == ChatRole.User);
        message.Contents.OfType<TextContent>().Last().Text += suffix;
    }

    private sealed class HookBridgeClient(string? toolName = null, string? toolCallId = null) : IChatClient
    {
        private int _requests;

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public string? ToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = messages.Select(message => message.Clone()).ToArray();
            Requests.Add(request);
            if (Interlocked.Increment(ref _requests) == 1 && toolName is not null)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent(toolCallId!, toolName,
                        new Dictionary<string, object?> { [toolName == "echo" ? "value" : "unused"] = "original" })]);
            }
            else
            {
                var result = request.SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>().SingleOrDefault();
                ToolResult = result?.Result?.ToString() ?? result?.Exception?.Message;
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TestExtensionUi : IPiSharpExtensionUi
    {
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public void Notify(string message, string type = "info") { }
    }
}
