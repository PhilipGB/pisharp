using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionSelectionTransitionTests
{
    [Fact]
    public async Task ActiveRunPublishesSelectionAfterOldRequestSettlesAndRuntimeActivates()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-selection-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<string>();
            var runtimeActivated = false;
            var selectionOwnerUpdated = false;
            var observerRanBeforeActivation = false;
            adapter.AddThinkingLevelSelectHandler((eventData, _, _) =>
            {
                observerRanBeforeActivation |= !runtimeActivated || !selectionOwnerUpdated;
                Assert.Equal(PiSharpThinkingLevel.High, eventData.Level);
                Assert.Equal(PiSharpThinkingLevel.Off, eventData.PreviousLevel);
                observed.Add("thinking_level_select");
                return ValueTask.CompletedTask;
            });
            adapter.AddModelSelectHandler((eventData, _, _) =>
            {
                observerRanBeforeActivation |= !runtimeActivated || !selectionOwnerUpdated;
                Assert.Equal("new-provider", eventData.Model.ProviderId);
                Assert.Equal("new-model", eventData.Model.ModelId);
                Assert.Equal("old-provider", eventData.PreviousModel!.ProviderId);
                Assert.Equal("old-model", eventData.PreviousModel.ModelId);
                Assert.Equal(PiSharpExtensionModelSelectionSource.Cycle, eventData.Source);
                observed.Add("model_select");
                return ValueTask.CompletedTask;
            });
            adapter.AddTurnEndHandler((eventData, _, _) =>
            {
                observed.Add($"turn_end:{eventData.TurnIndex}");
                return ValueTask.CompletedTask;
            });
            adapter.AddTurnStartHandler((eventData, _, _) =>
            {
                observed.Add($"turn_start:{eventData.TurnIndex}");
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = new ConversationSession(workingDirectory, "old-model", null, "old-provider");
            conversation.SelectModel("old-model", null, "old-provider");
            var client = new BlockingClient();
            var nextClient = new SelectionBoundaryClient(observed);
            var transitionTool = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "tool complete",
                name: "transition_tool");
            var agent = new PiAgent(client, new CodingTools(workingDirectory), noBuiltinTools: true,
                extensionToolRegistrations: [new(transitionTool)],
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation, provider: "old-provider", reasoningLevel: "off");

            var task = Task.Run(async () =>
            {
                await foreach (var _ in run.RunEventsAsync("finish current request")) { }
            });
            await client.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var previous = new PiSharpExtensionModelInfo("old-provider", "old-model", "old-model", null, false);
            var current = new PiSharpExtensionModelInfo("new-provider", "new-model", "new-model", null, true);
            Assert.True(run.TrySetModelDuringRun("new-model", null, "new-provider", null, null, 0, "high",
                new ReasoningOptions { Effort = ReasoningEffort.High }, () =>
                {
                    runtimeActivated = true;
                    agent.SetModelRuntime(nextClient, supportsImages: true, imageResizeOptions: null);
                },
                providerApi: "test-api",
                notifyAfterActivation: async token =>
                {
                    await agent.ExtensionEvents.DispatchThinkingLevelSelectAsync(
                        new(PiSharpThinkingLevel.High, PiSharpThinkingLevel.Off), token);
                    await agent.ExtensionEvents.DispatchModelSelectAsync(new(current, previous,
                        PiSharpExtensionModelSelectionSource.Cycle), token);
                },
                updateSelectionOwner: () => selectionOwnerUpdated = true));

            Assert.False(runtimeActivated);
            Assert.True(selectionOwnerUpdated);
            Assert.Equal(["turn_start:0"], observed);

            client.ReleaseRequest.TrySetResult(true);
            await task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(runtimeActivated);
            Assert.True(selectionOwnerUpdated);
            Assert.False(observerRanBeforeActivation);
            Assert.True(nextClient.SawSelectionBeforeRequest,
                "The replacement provider request started before the committed selection notifications.");
            Assert.True(nextClient.SawFinalizedToolResult);
            Assert.Equal(["turn_start:0", "turn_end:0", "thinking_level_select", "model_select", "turn_start:1",
                "next_provider_request", "turn_end:1"], observed);
            Assert.Equal("new-model", conversation.Model);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    private sealed class BlockingClient : IChatClient
    {
        public TaskCompletionSource<bool> RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "unused")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestStarted.TrySetResult(true);
            await ReleaseRequest.Task.WaitAsync(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new FunctionCallContent("transition-tool-call", "transition_tool", new Dictionary<string, object?>())])
            {
                FinishReason = ChatFinishReason.ToolCalls
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SelectionBoundaryClient(List<string> observed) : IChatClient
    {
        public bool SawSelectionBeforeRequest { get; private set; }
        public bool SawFinalizedToolResult { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            observed.Add("next_provider_request");
            SawSelectionBeforeRequest = observed.Contains("thinking_level_select") &&
                observed.Contains("model_select") && observed.Contains("turn_start:1");
            SawFinalizedToolResult = messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>().Any(result => result.CallId == "transition-tool-call");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "completed");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
