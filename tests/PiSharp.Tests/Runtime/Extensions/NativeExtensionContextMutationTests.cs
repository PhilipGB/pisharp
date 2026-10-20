using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class NativeExtensionContextMutationTests
{
    [Fact]
    public async Task StagesRunInOrderAndRestoreCurrentSystemStateAfterVisibleMessagesChange()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var order = new List<string>();
        adapter.AddContextHandler((eventData, _, _) =>
        {
            order.Add("context-a");
            Assert.DoesNotContain(eventData.Messages, message => message.Role == ChatRole.System);
            eventData.Messages.RemoveAt(eventData.Messages.Count - 1);
            return ValueTask.CompletedTask;
        });
        adapter.AddContextHandler((eventData, _, _) =>
        {
            order.Add("context-b");
            Assert.DoesNotContain(eventData.Messages, message => message.Role == ChatRole.System);
            Assert.Single(eventData.Messages);
            return ValueTask.CompletedTask;
        });
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            order.Add("context_with_system");
            Assert.Equal("base prompt\n\nextra section", eventData.Messages[0].Text);
            Assert.Equal(["tool_b"], eventData.ToolNames);
            eventData.SystemInstructions = "edited prompt";
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var originalSystem = new ChatMessage(ChatRole.System, "base prompt");
        SystemMessageTranscript.ImportMetadata(originalSystem, JsonSerializer.SerializeToElement(new
        {
            toolsAdded = new[] { new { name = "tool_a", description = "A", inputSchema = new { type = "object" } } }
        }));
        var systemUpdate = new ChatMessage(ChatRole.System, "extra section");
        SystemMessageTranscript.ImportMetadata(systemUpdate, JsonSerializer.SerializeToElement(new
        {
            toolsAdded = new[] { new { name = "tool_b", description = "B", inputSchema = new { type = "object" } } },
            toolsRemoved = new[] { new { name = "tool_a" } }
        }));
        ChatMessage[] input =
        [
            originalSystem,
            new(ChatRole.User, "keep this"),
            systemUpdate,
            new(ChatRole.Assistant, "remove this")
        ];
        var tool = AIFunctionFactory.Create((string value) => value, name: "tool_b");
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });

        var result = await dispatcher.DispatchContextMutationsAsync(input, "host prompt", [tool], CancellationToken.None);

        Assert.Equal(["context-a", "context-b", "context_with_system"], order);
        Assert.Equal("edited prompt", result.SystemInstructions);
        Assert.True(result.MessagesChanged);
        Assert.Equal([ChatRole.System, ChatRole.User], result.Messages.Select(message => message.Role));
        Assert.Equal("base prompt\n\nextra section", result.Messages[0].Text);
        Assert.Equal(["tool_b"], SystemMessageTranscript.CurrentTools(result.Messages)
            .Select(declaration => declaration.GetProperty("name").GetString()));
        Assert.Equal(4, input.Length);
        Assert.Equal("remove this", input[^1].Text);
    }

    [Fact]
    public async Task StructuralContextEditKeepsRevisionBoundaryWhenRetainedMessageContentChanges()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User],
                eventData.Messages.Select(message => message.Role));
            eventData.Messages[0].Contents.OfType<TextContent>().Single().Text = "User A edited";
            eventData.Messages.RemoveAt(1);
            eventData.Messages[1].Contents.OfType<TextContent>().Single().Text = "User C edited";
            eventData.SystemInstructions = "fresh instructions";
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var toolA = AIFunctionFactory.Create(() => "A", name: "tool_a", description: "tool A");
        var toolB = AIFunctionFactory.Create(() => "B", name: "tool_b", description: "tool B");
        var initial = new ChatMessage(ChatRole.System, "stale instructions");
        SystemMessageTranscript.ImportMetadata(initial, JsonSerializer.SerializeToElement(new
        {
            toolsAdded = new[] { ToolDeclarationJson.Create(toolA) }
        }));
        var update = new ChatMessage(ChatRole.System, "");
        SystemMessageTranscript.ImportMetadata(update, JsonSerializer.SerializeToElement(new
        {
            toolsRemoved = new[] { new { name = "tool_a" } },
            toolsAdded = new[] { ToolDeclarationJson.Create(toolB) }
        }));
        var transcript = JsonSerializer.SerializeToElement(new[]
        {
            new { afterMessageCount = 0, message = JsonSerializer.SerializeToElement(initial, AIJsonUtilities.DefaultOptions) },
            new { afterMessageCount = 2, message = JsonSerializer.SerializeToElement(update, AIJsonUtilities.DefaultOptions) }
        });
        ChatMessage[] input =
        [
            new(ChatRole.User, "User A"),
            new(ChatRole.Assistant, "Assistant B"),
            new(ChatRole.User, "User C")
        ];
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });

        var result = await dispatcher.DispatchContextMutationsAsync(input, "stale instructions", [toolB],
            CancellationToken.None);
        var projected = ToolDeclarationTranscript.ApplyContextMutation(transcript, input.Length, result.MessageLineage,
            result.SystemInstructions, instructionsChanged: true,
            messageStructureChanged: result.MessageStructureChanged, removeSystemContext: false);

        var revisions = Assert.IsType<JsonElement>(projected).EnumerateArray().ToArray();
        Assert.Equal([0, 1], revisions.Select(revision => revision.GetProperty("afterMessageCount").GetInt32()));
        Assert.Equal("User A", input[0].Text);
        Assert.Equal("Assistant B", input[1].Text);
        Assert.Equal("User C", input[2].Text);
        Assert.Equal("User A edited", result.Messages[0].Text);
        Assert.Equal("User C edited", result.Messages[1].Text);
    }

    [Fact]
    public async Task FirstContextStageTracksRetainedMessageIdentityAcrossContentEditsAndRemoval()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddContextHandler((eventData, _, _) =>
        {
            eventData.Messages[0].Contents.OfType<TextContent>().Single().Text = "User A edited";
            eventData.Messages.RemoveAt(1);
            eventData.Messages[1].Contents.OfType<TextContent>().Single().Text = "User C edited";
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        ChatMessage[] input =
        [
            new(ChatRole.User, "User A"),
            new(ChatRole.Assistant, "Assistant B"),
            new(ChatRole.User, "User C")
        ];
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });

        var result = await dispatcher.DispatchContextMutationsAsync(input, "prompt", [], CancellationToken.None);

        Assert.Equal([0, 2], result.MessageLineage);
        Assert.Equal([ChatRole.User, ChatRole.User], result.Messages.Select(message => message.Role));
        Assert.Equal("User A", input[0].Text);
        Assert.Equal("Assistant B", input[1].Text);
        Assert.Equal("User C", input[2].Text);
    }

    [Fact]
    public async Task DuplicateContentDoesNotConfuseLineageWhenTheRetainedMessageIsEdited()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            eventData.Messages.RemoveAt(0);
            eventData.Messages[0].Contents.OfType<TextContent>().Single().Text = "second duplicate edited";
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        ChatMessage[] input =
        [
            new(ChatRole.User, "duplicate"),
            new(ChatRole.User, "duplicate"),
            new(ChatRole.User, "tail")
        ];
        var firstRevision = new ChatMessage(ChatRole.System, "prompt");
        var secondRevision = new ChatMessage(ChatRole.System, "tool update");
        var transcript = JsonSerializer.SerializeToElement(new[]
        {
            new { afterMessageCount = 0, message = JsonSerializer.SerializeToElement(firstRevision, AIJsonUtilities.DefaultOptions) },
            new { afterMessageCount = 1, message = JsonSerializer.SerializeToElement(secondRevision, AIJsonUtilities.DefaultOptions) }
        });
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });

        var result = await dispatcher.DispatchContextMutationsAsync(input, "prompt", [], CancellationToken.None);
        var projected = ToolDeclarationTranscript.ApplyContextMutation(transcript, input.Length,
            result.MessageLineage, "prompt", instructionsChanged: false,
            messageStructureChanged: result.MessageStructureChanged, removeSystemContext: false);

        Assert.Equal([1, 2], result.MessageLineage);
        var revisions = Assert.IsType<JsonElement>(projected).EnumerateArray().ToArray();
        Assert.Equal([0, 0], revisions.Select(revision => revision.GetProperty("afterMessageCount").GetInt32()));
        Assert.Equal("second duplicate edited", result.Messages[0].Text);
        Assert.Equal("tail", result.Messages[1].Text);
    }

    [Fact]
    public async Task InsertedReorderedAndReplacementMessagesHaveOnlyKnownLineage()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        ChatMessage? inserted = null;
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            inserted = new ChatMessage(ChatRole.User, "new message");
            eventData.Messages = [eventData.Messages[2], eventData.Messages[0], inserted];
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        ChatMessage[] input =
        [new(ChatRole.User, "A"), new(ChatRole.User, "B"), new(ChatRole.User, "C")];
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });

        var result = await dispatcher.DispatchContextMutationsAsync(input, "prompt", [], CancellationToken.None);

        Assert.Equal([2, 0, null], result.MessageLineage);
        Assert.True(result.MessageStructureChanged);
        Assert.Equal(["C", "A", "new message"], result.Messages.Select(message => message.Text));
        Assert.NotSame(inserted, result.Messages[2]);
    }

    [Fact]
    public async Task InvalidHandlerOutputRollsBackAndLaterHandlersContinueWithSourceDiagnostics()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        registration.SetCurrentSourceInfo(Source("/extensions/bad-output.dll"));
        adapter.AddContextHandler((eventData, _, _) =>
        {
            eventData.Messages.Add(null!);
            return ValueTask.CompletedTask;
        });
        registration.SetCurrentSourceInfo(Source("/extensions/good-output.dll"));
        adapter.AddContextHandler((eventData, _, _) =>
        {
            Assert.Equal("original", eventData.Messages[0].Text);
            eventData.Messages[0].Contents[0].ShouldBeTextContent().Text = "continued";
            return ValueTask.CompletedTask;
        });
        registration.SetCurrentSourceInfo(null);
        adapter.Close();
        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);

        var result = await dispatcher.DispatchContextMutationsAsync(
            [new(ChatRole.User, "original")], "system", [], CancellationToken.None);

        Assert.Equal("continued", Assert.Single(result.Messages).Text);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("extension_error", diagnostic.Type);
        Assert.Contains("bad-output.dll", JsonSerializer.Serialize(diagnostic.Details), StringComparison.Ordinal);
        Assert.Contains("context", JsonSerializer.Serialize(diagnostic.Details), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidFullContextOutputRollsBackBeforeLaterHandlers()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            eventData.Messages.Add(null!);
            eventData.SystemInstructions = "invalid handler prompt";
            return ValueTask.CompletedTask;
        });
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            Assert.Equal("host prompt", eventData.SystemInstructions);
            Assert.Equal("original", Assert.Single(eventData.Messages).Text);
            eventData.SystemInstructions = "accepted handler prompt";
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);

        var result = await dispatcher.DispatchContextMutationsAsync(
            [new(ChatRole.User, "original")], "host prompt", [], CancellationToken.None);

        Assert.Equal("accepted handler prompt", result.SystemInstructions);
        Assert.Equal("original", Assert.Single(result.Messages).Text);
        Assert.Single(diagnostics, item => item.Type == "extension_error");
    }

    [Fact]
    public async Task FullContextCanRemoveLeadingSystemMessageAndEmitsNonfatalDiagnostic()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        registration.SetCurrentSourceInfo(Source("/extensions/system-remover.dll"));
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            eventData.Messages = [eventData.Messages[1]];
            eventData.SystemInstructions = null;
            return ValueTask.CompletedTask;
        });
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            Assert.DoesNotContain(eventData.Messages, message => message.Role == ChatRole.System);
            eventData.Messages.Insert(0, new ChatMessage(ChatRole.System, "later restoration"));
            return ValueTask.CompletedTask;
        });
        registration.SetCurrentSourceInfo(null);
        adapter.Close();
        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);

        var result = await dispatcher.DispatchContextMutationsAsync(
            [new(ChatRole.System, "system"), new(ChatRole.User, "prompt")], "system", [], CancellationToken.None);

        Assert.Equal([ChatRole.System, ChatRole.User], result.Messages.Select(message => message.Role));
        Assert.Equal("later restoration", result.Messages[0].Text);
        var warning = Assert.Single(diagnostics);
        Assert.Equal("extension_warning", warning.Type);
        Assert.Contains("system-remover.dll", JsonSerializer.Serialize(warning.Details), StringComparison.Ordinal);
        Assert.Contains("context_with_system", JsonSerializer.Serialize(warning.Details), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingSeparatelyHeldSystemInstructionsEmitsDiagnosticWithoutFabricatingSystemMessage()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            Assert.DoesNotContain(eventData.Messages, message => message.Role == ChatRole.System);
            eventData.SystemInstructions = null;
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);

        var result = await dispatcher.DispatchContextMutationsAsync(
            [new(ChatRole.User, "prompt")], "host instructions", [], CancellationToken.None);

        Assert.Null(result.SystemInstructions);
        Assert.Equal(ChatRole.User, Assert.Single(result.Messages).Role);
        Assert.Equal("extension_warning", Assert.Single(diagnostics).Type);
    }

    [Fact]
    public async Task NativeHandlersReceiveDetachedMutableMessageMetadata()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddContextHandler((eventData, _, _) =>
        {
            var metadata = Assert.IsType<List<string>>(eventData.Messages[0].AdditionalProperties!["labels"]);
            metadata.Add("extension");
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        var originalLabels = new List<string> { "host" };
        var message = new ChatMessage(ChatRole.User, "prompt")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["labels"] = originalLabels }
        };
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });

        var result = await dispatcher.DispatchContextMutationsAsync([message], null, [], CancellationToken.None);

        Assert.Equal(["host"], originalLabels);
        Assert.Equal(["host", "extension"],
            Assert.IsType<List<string>>(Assert.Single(result.Messages).AdditionalProperties!["labels"]));
    }

    [Fact]
    public void RegistrationCheckpointAndSourceRemovalIncludeBothContextStages()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var source = Source("/extensions/context.dll");
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });
        var restore = registration.CreateConfigurationCheckpoint();
        registration.SetCurrentSourceInfo(source);
        adapter.AddContextHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddContextWithSystemHandler((_, _, _) => ValueTask.CompletedTask);
        registration.SetCurrentSourceInfo(null);
        adapter.Close();
        Assert.True(dispatcher.HasContextMutationHandlers);

        restore();
        Assert.False(dispatcher.HasContextMutationHandlers);
        var replacementAdapter = new NativeExtensionRegistrationAdapter(registration);
        registration.SetCurrentSourceInfo(source);
        replacementAdapter.AddContextHandler((_, _, _) => ValueTask.CompletedTask);
        replacementAdapter.AddContextWithSystemHandler((_, _, _) => ValueTask.CompletedTask);
        registration.SetCurrentSourceInfo(null);
        replacementAdapter.Close();
        registration.ReconcileExtension(source, null);

        Assert.False(dispatcher.HasContextMutationHandlers);
    }

    [Fact]
    public async Task ExtensionSourceOrderIsPreservedWithinBothStages()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var order = new List<string>();
        var extensionA = Source("/extensions/a.dll");
        var extensionB = Source("/extensions/b.dll");
        registration.SetCurrentSourceInfo(extensionB);
        adapter.AddContextHandler((_, _, _) => { order.Add("B1"); return ValueTask.CompletedTask; });
        adapter.AddContextHandler((_, _, _) => { order.Add("B2"); return ValueTask.CompletedTask; });
        adapter.AddContextWithSystemHandler((_, _, _) => { order.Add("B3"); return ValueTask.CompletedTask; });
        registration.SetCurrentSourceInfo(extensionA);
        adapter.AddContextHandler((_, _, _) => { order.Add("A1"); return ValueTask.CompletedTask; });
        adapter.AddContextWithSystemHandler((_, _, _) => { order.Add("A2"); return ValueTask.CompletedTask; });
        registration.SetCurrentSourceInfo(null);
        adapter.Close();
        registration.OrderExtensions([extensionA, extensionB]);
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });

        await dispatcher.DispatchContextMutationsAsync([new(ChatRole.User, "prompt")], "system", [],
            CancellationToken.None);

        Assert.Equal(["A1", "B1", "B2", "A2", "B3"], order);
    }

    [Fact]
    public async Task ContextStageAcceptsReturnedMessageListAndPassesItToNextHandler()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        adapter.AddContextHandler((eventData, _, _) =>
        {
            eventData.Messages = [new ChatMessage(ChatRole.User, "replacement")];
            return ValueTask.CompletedTask;
        });
        adapter.AddContextHandler((eventData, _, _) =>
        {
            Assert.Equal("replacement", Assert.Single(eventData.Messages).Text);
            eventData.Messages[0].Contents[0].ShouldBeTextContent().Text += " accepted";
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });

        var result = await dispatcher.DispatchContextMutationsAsync(
            [new(ChatRole.User, "original")], "system", [], CancellationToken.None);

        Assert.Equal("replacement accepted", Assert.Single(result.Messages).Text);
    }

    [Fact]
    public async Task ContextMutationRunsAfterRoutePreparationAndOnlyOnceForEquivalentRetries()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var contextCalls = 0;
        var fullContextCalls = 0;
        adapter.AddContextHandler((eventData, _, _) =>
        {
            contextCalls++;
            Assert.Equal("route prepared", eventData.Messages[^1].Text);
            eventData.Messages[^1].Contents[0].ShouldBeTextContent().Text += " [native]";
            return ValueTask.CompletedTask;
        });
        adapter.AddContextWithSystemHandler((eventData, _, _) =>
        {
            fullContextCalls++;
            Assert.Equal("host prompt", eventData.SystemInstructions);
            Assert.Equal(["tool_x"], eventData.ToolNames);
            eventData.SystemInstructions = "extension prompt";
            return ValueTask.CompletedTask;
        });
        adapter.Close();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), _ => { });
        var physical = new RecordingClient();
        var routed = new RoutedChatClient(new MutableChatClient(new RecordingClient()), _ => { },
            contextMutationDispatcher: dispatcher);
        routed.SetSession(new ConversationSession(Path.GetTempPath(), "auto", null, provider: "router"), "off", null);
        var route = new VirtualModelRequestRoute(physical,
            new ModelDescriptor("physical", null, 32000, null, Provider: "provider", Input: ["text"], Api: "test-api"),
            "provider", "off", null, null, null);
        routed.SetRouter(_ => Task.FromResult(route));
        routed.SetContextPreparation((_, _, _, _) => Task.FromResult<IReadOnlyList<ChatMessage>>(
            [new(ChatRole.User, "route prepared")]));
        var tool = AIFunctionFactory.Create((string value) => value, name: "tool_x");
        var options = NativeContextMutationRequest.Attach(new ChatOptions
        {
            Instructions = "host prompt",
            Tools = [tool]
        }, new NativeContextMutationRequest());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var hinted = VirtualModelRequestHints.WithHint(options, new("user", "off"));
            await routed.GetResponseAsync([new(ChatRole.User, "source")], hinted);
        }

        Assert.Equal(1, contextCalls);
        Assert.Equal(1, fullContextCalls);
        Assert.Equal(2, physical.Requests.Count);
        foreach (var request in physical.Requests)
        {
            Assert.Equal("route prepared [native]", Assert.Single(request.Messages).Text);
            Assert.Equal("extension prompt", request.Options!.Instructions);
            Assert.False(request.Options.AdditionalProperties?.ContainsKey(NativeContextMutationRequest.PropertyName));
        }
        Assert.Equal("host prompt", options.Instructions);
    }

    [Fact]
    public async Task NativeStagesRunForInitialAndToolLoopRequestsWithoutChangingCanonicalHistory()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-native-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var contextCalls = 0;
            var fullContextCalls = 0;
            adapter.AddContextHandler((eventData, _, _) =>
            {
                contextCalls++;
                Assert.DoesNotContain(eventData.Messages, message => message.Role == ChatRole.System);
                eventData.Messages.Last(message => message.Role == ChatRole.User)
                    .Contents.OfType<TextContent>().Last().Text += " [native]";
                return ValueTask.CompletedTask;
            });
            adapter.AddContextWithSystemHandler((eventData, _, _) =>
            {
                fullContextCalls++;
                Assert.Contains("base system prompt", eventData.SystemInstructions);
                Assert.Equal(["echo_ext"], eventData.ToolNames);
                eventData.SystemInstructions = "native system prompt";
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var echo = AIFunctionFactory.Create((string value) => "echo: " + value, name: "echo_ext");
            var provider = new ToolLoopClient();
            var agent = new PiAgent(provider, new CodingTools(workingDirectory), selectedTools: ["echo_ext"],
                extensionTools: [echo], systemPrompt: "base system prompt", liveExtensionRegistration: registration);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await foreach (var _ in run.RunEventsAsync("hello")) { }

            Assert.Equal(2, contextCalls);
            Assert.Equal(2, fullContextCalls);
            Assert.Equal(2, provider.Requests.Count);
            Assert.All(provider.Requests, request => Assert.Equal("native system prompt", request.Options!.Instructions));
            Assert.Contains("hello [native]", provider.Requests[0].Messages.Last(message => message.Role == ChatRole.User).Text);
            Assert.Contains("hello [native]", provider.Requests[1].Messages.Last(message => message.Role == ChatRole.User).Text);
            Assert.Contains(provider.Requests[1].Messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>(), result => result.CallId == "tool-call");
            Assert.DoesNotContain(run.Conversation.ContextMessages(), message =>
                message.Text.Contains("[native]", StringComparison.Ordinal));
            var transcript = Assert.IsType<JsonElement>(provider.Requests[^1].Options!.AdditionalProperties!["pisharp.toolTranscript"]);
            var initialSystem = Assert.Single(transcript.EnumerateArray()).GetProperty("message")
                .Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)!;
            Assert.Equal("native system prompt", initialSystem.Text);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task CancellationRevokesCallbackContextAndStopsContextDispatch()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var started = new TaskCompletionSource<(NativeExtensions.PiSharpExtensionContext Context, CancellationToken Token)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.AddContextWithSystemHandler(async (_, context, token) =>
        {
            started.TrySetResult((context, token));
            await release.Task;
        });
        adapter.Close();
        var runtimeContext = CreateContext();
        runtimeContext.SetSessionContextFactory(_ => new NativeExtensions.PiSharpExtensionSessionContext(
            () => new("session", null, null, []),
            (_, _, _) => throw new InvalidOperationException("revoked session operation")));
        runtimeContext.SetCapabilitiesFactory((_, session) =>
            new NativeExtensions.PiSharpExtensionCapabilities(null, session, null));
        var dispatcher = new NativeExtensionEventDispatcher(registration, runtimeContext, _ => { });
        using var cancellation = new CancellationTokenSource();
        var dispatch = dispatcher.DispatchContextMutationsAsync([new(ChatRole.User, "prompt")], "system", [],
            cancellation.Token);
        var callback = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var session = callback.Context.Session!;

        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(callback.Token.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => _ = session.Snapshot);
        }
        finally { release.TrySetResult(); }
    }

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateContext() => new(
        new PiSharpExtensionContextConfiguration("/tmp", "test", HasUserInterface: false,
            IsProjectTrusted: true, new TestUi()), static () => null, static () => [], static () => true);

    private static ResourceSourceInfo Source(string path) =>
        new(path, "native-package", "user", "package", "/extensions");

    private sealed class TestUi : IPiSharpExtensionUi
    {
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public void Notify(string message, string type = "info") { }
    }

    private sealed class RecordingClient : IChatClient
    {
        internal List<(ChatMessage[] Messages, ChatOptions? Options)> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((messages.ToArray(), options?.Clone()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answer")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ToolLoopClient : IChatClient
    {
        internal List<(ChatMessage[] Messages, ChatOptions? Options)> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add((messages.Select(PiSharpContextTransformPipeline.CloneMessage).ToArray(), options?.Clone()));
            if (Requests.Count == 1)
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("tool-call", "echo_ext", new Dictionary<string, object?> { ["value"] = "input" })]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

internal static class ContextMutationTestContentExtensions
{
    internal static TextContent ShouldBeTextContent(this AIContent content) =>
        Assert.IsType<TextContent>(content);
}
