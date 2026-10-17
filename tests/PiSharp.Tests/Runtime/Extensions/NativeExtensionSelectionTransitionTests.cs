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
            adapter.Close();

            var conversation = new ConversationSession(workingDirectory, "old-model", null, "old-provider");
            conversation.SelectModel("old-model", null, "old-provider");
            var client = new BlockingClient();
            var agent = new PiAgent(client, new CodingTools(workingDirectory), noTools: true,
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
                new ReasoningOptions { Effort = ReasoningEffort.High }, () => runtimeActivated = true,
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
            Assert.Empty(observed);

            client.ReleaseRequest.TrySetResult(true);
            await task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(runtimeActivated);
            Assert.True(selectionOwnerUpdated);
            Assert.False(observerRanBeforeActivation);
            Assert.Equal(["thinking_level_select", "model_select"], observed);
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
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("completed")])
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
