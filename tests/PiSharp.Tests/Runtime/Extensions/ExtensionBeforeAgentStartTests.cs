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
                [new TextContent("hello"), new DataContent(Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="), "image/png")]);
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
    public async Task StructuredPromptOptionsComposeInOrderAndFullReplacementWins()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-before-agent-prompt-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            string? replacement = null;
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                Assert.Contains("preamble", eventData.SystemPromptOptions.Sections.Keys);
                eventData.SystemPromptOptions.Sections["extension_rules"] = "first handler guidance";
                eventData.SystemPromptOptions.Sections.Remove("docs");
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                Assert.Contains("first handler guidance", eventData.SystemPrompt, StringComparison.Ordinal);
                eventData.SystemPromptOptions.Sections["tools"] = "changed by second handler";
                replacement = eventData.SystemPrompt + "\nwhole prompt replacement";
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(new(replacement));
            });
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                Assert.Equal(replacement, eventData.SystemPrompt);
                eventData.SystemPromptOptions.Sections["late_rules"] = "not rendered after replacement";
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });

            var provider = new BeforeAgentStartClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), systemPrompt: "base prompt",
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent, new ConversationSession(workingDirectory, "test-model", null));
            await foreach (var _ in run.RunEventsAsync("hello")) { }

            var instructions = Assert.Single(provider.Instructions);
            Assert.Contains("<extension_rules>\nfirst handler guidance\n</extension_rules>", instructions,
                StringComparison.Ordinal);
            Assert.DoesNotContain("<docs>", instructions, StringComparison.Ordinal);
            Assert.EndsWith("whole prompt replacement", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("changed by second handler", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("late_rules", instructions, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StructuredPromptOptionsAreRequestLocalAndDoNotMultiplyAcrossRetry()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-before-agent-prompt-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var registration = new ExtensionRegistration();
            var handlerCalls = 0;
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                handlerCalls++;
                if (eventData.Prompt == "first")
                {
                    eventData.SystemPromptOptions.Sections["rules"] = "answer with care";
                    eventData.SystemPromptOptions.Sections["extension_rules"] = "one prompt addition";
                }
                else
                {
                    Assert.DoesNotContain("one prompt addition", eventData.SystemPrompt, StringComparison.Ordinal);
                }
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
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

            await foreach (var _ in run.RunEventsAsync("first")) { }
            Assert.Equal(2, provider.Instructions.Count);
            Assert.All(provider.Instructions, instructions =>
            {
                Assert.Contains("<rules>\nanswer with care\n</rules>", instructions, StringComparison.Ordinal);
                Assert.Contains("one prompt addition", instructions, StringComparison.Ordinal);
                Assert.Equal(1, instructions.Split("one prompt addition", StringSplitOptions.None).Length - 1);
            });
            Assert.Equal(1, handlerCalls);

            await foreach (var _ in run.RunEventsAsync("second")) { }
            Assert.Equal(3, provider.Instructions.Count);
            Assert.DoesNotContain("one prompt addition", provider.Instructions[2], StringComparison.Ordinal);
            Assert.Equal(2, handlerCalls);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StructuredPromptOptionsApplyToIdleCustomTriggeredTurn()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-prompt-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                Assert.Equal(PiSharpTurnMessageType.Custom, eventData.MessageType);
                eventData.SystemPromptOptions.Sections["trigger_context"] = "background work finished";
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });
            var provider = new BeforeAgentStartClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
                    beforeAgentStartHandlers: registration.BeforeAgentStartHandlers),
                new ConversationSession(workingDirectory, "test-model", null));

            await foreach (var _ in run.RunCustomMessageEventsAsync(
                               new PiSharp.Extensions.Abstractions.PiSharpCustomMessage(
                                   "job-finished", "background work finished"), sourcePath: null)) { }

            var instructions = Assert.Single(provider.Instructions);
            Assert.Contains("<trigger_context>\nbackground work finished\n</trigger_context>", instructions,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StructuredPromptOptionsStayConsistentWhenActiveToolsChangeMidRun()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-prompt-options-loadout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var switchTool = AIFunctionFactory.Create((AIFunctionArguments arguments) =>
            {
                PiSharpToolExecutionContext.Get(arguments)!.SetActiveTools(["deferred"]);
                return "activated";
            }, name: "switch_tools");
            var deferredTool = AIFunctionFactory.Create(() => "deferred", name: "deferred");
            var registration = new ExtensionRegistration();
            var setupPrompts = new List<string>();
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                setupPrompts.Add(eventData.SystemPrompt);
                eventData.SystemPromptOptions.Sections["extension_rules"] = "keep this guidance";
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });
            var provider = new ToolLoadoutPromptClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), noBuiltinTools: true,
                extensionToolRegistrations:
                [
                    new(switchTool, ToolExposure.ModelOnly, PromptSnippet: "Switch active tools."),
                    new(deferredTool, ToolExposure.Deferred, PromptSnippet: "Run a deferred operation.")
                ],
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers);
            var run = await ConversationRun.OpenAsync(agent,
                new ConversationSession(workingDirectory, "test-model", null));

            await foreach (var _ in run.RunEventsAsync("switch tools")) { }

            Assert.Equal(["switch_tools"], provider.ToolNames[0]);
            Assert.Equal(["deferred"], provider.ToolNames[1]);
            Assert.Contains("- switch_tools: Switch active tools.", provider.Instructions[0], StringComparison.Ordinal);
            Assert.Contains("- deferred: Run a deferred operation.", provider.Instructions[1], StringComparison.Ordinal);
            Assert.DoesNotContain("- switch_tools:", provider.Instructions[1], StringComparison.Ordinal);
            Assert.Equal(1, provider.Instructions[1].Split("keep this guidance", StringSplitOptions.None).Length - 1);

            await foreach (var _ in run.RunEventsAsync("follow up")) { }

            Assert.Contains("- deferred: Run a deferred operation.", setupPrompts[1], StringComparison.Ordinal);
            Assert.DoesNotContain("- switch_tools:", setupPrompts[1], StringComparison.Ordinal);
            Assert.Contains("- deferred: Run a deferred operation.", provider.Instructions[2], StringComparison.Ordinal);
            Assert.DoesNotContain("- switch_tools:", provider.Instructions[2], StringComparison.Ordinal);
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

            Assert.False(await run.DeliverExtensionCustomMessageAsync(
                new PiSharp.Extensions.Abstractions.PiSharpCustomMessage("background-note", "ready for later"),
                "/extensions/example.cs", triggerTurn: false,
                deliveryMode: PiSharp.Extensions.Abstractions.PiSharpMessageDeliveryMode.Steer));
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
    public async Task TriggeredCustomMessageExecutesOnceAcrossAutomaticProviderRetry()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-trigger-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var registration = new ExtensionRegistration();
            var beforeStartCount = 0;
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                beforeStartCount++;
                Assert.Equal(PiSharpTurnMessageType.Custom, eventData.MessageType);
                Assert.Equal("one logical trigger", eventData.StartingCustomMessage?.Content);
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });
            var provider = new RetryThenCompleteClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
                    retryPolicy: ProviderRetryPolicy.None,
                    beforeAgentStartHandlers: registration.BeforeAgentStartHandlers), conversation,
                retryPolicy: new AgentRunRetryPolicy(maxRetries: 1, baseDelay: TimeSpan.Zero,
                    maxDelay: TimeSpan.Zero),
                retryDelay: static (_, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunCustomMessageEventsAsync(
                               new PiSharp.Extensions.Abstractions.PiSharpCustomMessage(
                                   "retry-trigger", "one logical trigger"), sourcePath: null))
                events.Add(item);

            Assert.Contains(events, item => item.Type == "turn_completed");
            Assert.Equal(2, provider.Requests.Count);
            Assert.All(provider.Requests, request => Assert.Equal(1, request.Count(message =>
                message.Role == ChatRole.User && message.Text == "one logical trigger")));
            Assert.Equal(1, conversation.Tree.ActivePath().Count(node => node.Type == "custom_message"));
            Assert.Equal(1, beforeStartCount);
            Assert.False(run.IsActive);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TriggeredCustomMessageResumesFromDiskWithoutReplayingItsTurn()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            conversation.Append(new ChatMessage(ChatRole.User, "seed before trigger"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "seed response"));
            var sessionPath = store.NewPath(conversation);
            await store.SaveAsync(conversation, sessionPath);
            var handlerCalls = 0;
            var registration = new ExtensionRegistration();
            registration.AddBeforeAgentStartHandler((eventData, _, _) =>
            {
                handlerCalls++;
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });

            var firstProvider = new BeforeAgentStartClient();
            var firstRun = await ConversationRun.OpenAsync(
                new PiAgent(firstProvider, new CodingTools(workingDirectory),
                    beforeAgentStartHandlers: registration.BeforeAgentStartHandlers),
                conversation, save: token => store.SaveAsync(conversation, sessionPath, token));
            await foreach (var _ in firstRun.RunCustomMessageEventsAsync(
                               new PiSharp.Extensions.Abstractions.PiSharpCustomMessage(
                                   "resume-notice", "background work completed", Display: false),
                               "/extensions/background.cs")) { }
            Assert.Equal(1, handlerCalls);
            Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");

            var compaction = Assert.IsType<ConversationSession.CompactionPlan>(
                conversation.PrepareCompaction(keepRecentTokens: 1));
            conversation.AppendCompaction(compaction, "The seed exchange was completed.", keepRecentTokens: 1);
            await store.SaveAsync(conversation, sessionPath);

            var resumedConversation = await store.LoadAsync(sessionPath);
            Assert.Single(resumedConversation.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.Contains(resumedConversation.ContextMessages(), message =>
                message.Text.Contains("seed exchange was completed", StringComparison.Ordinal));
            var forkableUser = Assert.Single(resumedConversation.ForkableUserMessages());
            var (forkedConversation, forkPrompt) = resumedConversation.ForkAtUser(forkableUser.Id);
            Assert.Equal("seed before trigger", forkPrompt);
            Assert.DoesNotContain(forkedConversation.Tree.ActivePath(), node => node.Type == "custom_message");
            var resumedProvider = new BeforeAgentStartClient();
            var resumedRun = await ConversationRun.OpenAsync(
                new PiAgent(resumedProvider, new CodingTools(workingDirectory),
                    beforeAgentStartHandlers: registration.BeforeAgentStartHandlers), resumedConversation);

            Assert.Equal(1, handlerCalls);
            Assert.Empty(resumedProvider.Requests);
            await foreach (var _ in resumedRun.RunEventsAsync("ordinary follow-up")) { }

            Assert.Equal(2, handlerCalls);
            var request = Assert.Single(resumedProvider.Requests);
            Assert.Contains(request, message =>
                message.Text.Contains("seed exchange was completed", StringComparison.Ordinal));
            Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "ordinary follow-up");
            Assert.Single(resumedConversation.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.False(resumedRun.IsActive);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task TriggeredCustomProviderFailureSettlesAndAllowsAnOrdinaryPrompt()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-provider-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var provider = new FailOnceThenCompleteClient();
            var beforeStartCount = 0;
            var registration = new ExtensionRegistration();
            registration.AddBeforeAgentStartHandler((_, _, _) =>
            {
                beforeStartCount++;
                return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
            });
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
                retryPolicy: ProviderRetryPolicy.None,
                beforeAgentStartHandlers: registration.BeforeAgentStartHandlers), conversation);
            var failedEvents = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunCustomMessageEventsAsync(
                               new PiSharp.Extensions.Abstractions.PiSharpCustomMessage(
                                   "provider-failure", "recover after failure"), sourcePath: null))
                failedEvents.Add(item);

            Assert.Contains(failedEvents, item => item.Type == "turn_failed");
            Assert.False(run.IsActive);
            Assert.Equal(1, beforeStartCount);
            Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");

            var recoveredEvents = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("ordinary recovery")) recoveredEvents.Add(item);

            Assert.Contains(recoveredEvents, item => item.Type == "turn_completed");
            Assert.Equal(2, provider.Requests.Count);
            Assert.Equal(2, beforeStartCount);
            Assert.Contains(provider.Requests[1], message => message.Role == ChatRole.User &&
                message.Text == "recover after failure");
            Assert.Contains(provider.Requests[1], message => message.Role == ChatRole.User &&
                message.Text == "ordinary recovery");
            Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.False(run.IsActive);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellingProviderExecutionForCustomTurnSettlesAndAllowsTheNextPrompt()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-provider-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var provider = new HeldFirstRequestClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
                retryPolicy: ProviderRetryPolicy.None), new ConversationSession(workingDirectory, "test-model", null));
            using var cancellation = new CancellationTokenSource();
            var events = new List<AgentLifecycleEvent>();
            var execution = Task.Run(async () =>
            {
                await foreach (var item in run.RunCustomMessageEventsAsync(
                                   new PiSharp.Extensions.Abstractions.PiSharpCustomMessage(
                                       "cancelled-provider", "cancel and recover"), sourcePath: null,
                                   cancellationToken: cancellation.Token))
                    events.Add(item);
            });
            await provider.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(run.IsActive);
            cancellation.Cancel();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(run.IsActive);
            Assert.Contains(events, item => item.Type is "turn_interrupted" or "prompt_rejected");
            Assert.Single(run.Conversation.Tree.ActivePath(), node => node.Type == "custom_message");
            await foreach (var _ in run.RunEventsAsync("next ordinary prompt")) { }
            Assert.Equal(2, provider.Requests.Count);
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
    public async Task BusyCustomMessageWithoutTriggerIsAppendedAfterToolResultsAndSeenByNaturalContinuation()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-context-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseTool = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hold = AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
            {
                toolStarted.TrySetResult();
                await releaseTool.Task.WaitAsync(cancellationToken);
                return "tool result";
            }, name: "hold");
            var registration = new ExtensionRegistration();
            registration.AddTool(new PiSharpToolRegistration(hold, ToolExposure.ModelOnly));
            var provider = new ToolThenCompleteClient();
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory),
                noBuiltinTools: true, extensionToolRegistrations: registration.ToolDefinitions), conversation);
            var execution = Task.Run(async () =>
            {
                await foreach (var _ in run.RunEventsAsync("start tool work")) { }
            });
            await toolStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(await run.DeliverExtensionCustomMessageAsync(
                new PiSharp.Extensions.Abstractions.PiSharpCustomMessage("background-note", "work completed"),
                "/extensions/background.cs", triggerTurn: false,
                deliveryMode: PiSharp.Extensions.Abstractions.PiSharpMessageDeliveryMode.FollowUp));
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
            releaseTool.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, provider.Requests.Count);
            var continuation = provider.Requests[1];
            var continuationToolResultIndex = Array.FindIndex(continuation, message => message.Role == ChatRole.Tool);
            var continuationCustomMessageIndexes = continuation.Select((message, index) => (message, index))
                .Where(entry => PiSharpCustomMessageProjection.TryRead(entry.message, out var custom) &&
                    custom.Content == "work completed")
                .Select(entry => entry.index).ToArray();
            Assert.True(continuationToolResultIndex >= 0);
            Assert.Single(continuationCustomMessageIndexes);
            Assert.True(continuationCustomMessageIndexes[0] > continuationToolResultIndex);
            var context = conversation.ContextMessages();
            var toolResultIndex = Array.FindIndex(context.ToArray(), message => message.Role == ChatRole.Tool);
            var customMessageIndex = Array.FindIndex(context.ToArray(), message =>
                PiSharpCustomMessageProjection.TryRead(message, out var custom) && custom.Content == "work completed");
            Assert.True(toolResultIndex >= 0);
            Assert.True(customMessageIndex > toolResultIndex);
            var customEntry = Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.Equal("/extensions/background.cs", customEntry.Payload.GetProperty("source").GetString());

            await foreach (var _ in run.RunEventsAsync("what finished?")) { }
            Assert.Equal(3, provider.Requests.Count);
            Assert.Contains(provider.Requests[2], message =>
                PiSharpCustomMessageProjection.TryRead(message, out var custom) && custom.Content == "work completed");
            Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BusyNextTurnCustomMessageWaitsForTheNextRunAfterItsStartingPrompt(bool triggerTurn)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-aside-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var provider = new HeldFirstRequestClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory)), conversation);
            var execution = Task.Run(async () =>
            {
                await foreach (var _ in run.RunEventsAsync("active prompt")) { }
            });
            await provider.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(await run.DeliverExtensionCustomMessageAsync(
                new PiSharp.Extensions.Abstractions.PiSharpCustomMessage("next-note", "hold for next run"),
                "/extensions/next.cs", triggerTurn,
                deliveryMode: PiSharp.Extensions.Abstractions.PiSharpMessageDeliveryMode.NextTurn));
            provider.ReleaseFirstRequest.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Single(provider.Requests);
            Assert.DoesNotContain(provider.Requests[0], message => message.Text == "hold for next run");
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "custom_message");

            await foreach (var _ in run.RunEventsAsync("ordinary next prompt")) { }

            var nextRunRequest = provider.Requests[1].Where(message => message.Role == ChatRole.User).ToArray();
            Assert.Equal(["active prompt", "ordinary next prompt", "hold for next run"],
                nextRunRequest.Select(message => message.Text));
            var customEntry = Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.Equal("/extensions/next.cs", customEntry.Payload.GetProperty("source").GetString());
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task IdleNextTurnCustomMessageWaitsAfterTheNextStartingPromptWithoutStartingARun()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-aside-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "test-model", null);
            var provider = new BeforeAgentStartClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(provider, new CodingTools(workingDirectory)), conversation);

            Assert.False(await run.DeliverExtensionCustomMessageAsync(
                new PiSharp.Extensions.Abstractions.PiSharpCustomMessage("next-note", "idle aside"),
                "/extensions/idle.cs", triggerTurn: true,
                deliveryMode: PiSharp.Extensions.Abstractions.PiSharpMessageDeliveryMode.NextTurn));
            Assert.Empty(provider.Requests);
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "custom_message");

            await foreach (var _ in run.RunEventsAsync("first ordinary prompt")) { }

            var request = Assert.Single(provider.Requests);
            Assert.Equal(["first ordinary prompt", "idle aside"], request
                .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
            var customEntry = Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "custom_message");
            Assert.Equal("/extensions/idle.cs", customEntry.Payload.GetProperty("source").GetString());
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
        public List<string> Instructions { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.ToArray());
            Instructions.Add(options?.Instructions ?? "");
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

    private sealed class ToolLoadoutPromptClient : IChatClient
    {
        public List<string[]> ToolNames { get; } = [];
        public List<string> Instructions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolNames.Add(options?.Tools?.Select(tool => tool.Name).ToArray() ?? []);
            Instructions.Add(options?.Instructions ?? string.Empty);
            if (ToolNames.Count == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("switch-call", "switch_tools", new Dictionary<string, object?>())]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
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

    private sealed class ToolThenCompleteClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = Requests.Count;
            Requests.Add(messages.ToArray());
            if (request == 0)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("hold-call", "hold", new Dictionary<string, object?>())]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FailOnceThenCompleteClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count == 1) throw new IOException("deterministic provider failure");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "recovered");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
