using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Composition;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class PiSharpExtensionSelectionEventsTests
{
    [Fact]
    public async Task EffectiveSelectionEmitsThinkingThenModelWithTheRequestedSource()
    {
        var registration = new ExtensionRegistration();
        var observed = new List<string>();
        registration.AddThinkingLevelSelectHandler((eventData, _, _) =>
        {
            observed.Add($"thinking:{eventData.PreviousLevel}>{eventData.Level}");
            return ValueTask.CompletedTask;
        });
        registration.AddModelSelectHandler((eventData, _, _) =>
        {
            observed.Add($"model:{eventData.Source}:{eventData.PreviousModel?.ProviderId}/{eventData.PreviousModel?.ModelId}>" +
                $"{eventData.Model.ProviderId}/{eventData.Model.ModelId}");
            return ValueTask.CompletedTask;
        });
        var agent = new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath()),
            liveExtensionRegistration: registration);
        var previous = Selection("first-provider", "first-model", "First model");
        var current = Selection("next-provider", "next-model", "Next model");

        await PiSharpExtensionSelectionEvents.PublishChangesAsync(agent, previous, "low", current, "high",
            PiSharpExtensionModelSelectionSource.Cycle, CancellationToken.None);
        await PiSharpExtensionSelectionEvents.PublishChangesAsync(agent, current, "high", current, "high",
            PiSharpExtensionModelSelectionSource.Set, CancellationToken.None);

        Assert.Equal(["thinking:Low>High",
            "model:Cycle:first-provider/first-model>next-provider/next-model"], observed);
    }

    [Fact]
    public async Task RestoreSourceIsPublishedOnlyWhenTheRestoredModelIdentityChanges()
    {
        var registration = new ExtensionRegistration();
        var observed = new List<PiSharpExtensionModelSelectEvent>();
        registration.AddModelSelectHandler((eventData, _, _) =>
        {
            observed.Add(eventData);
            return ValueTask.CompletedTask;
        });
        var agent = new PiAgent(new StubClient(), new CodingTools(Path.GetTempPath()),
            liveExtensionRegistration: registration);
        var initial = Selection("fixture", "default", "Default");
        var restored = Selection("fixture", "saved", "Saved");

        await PiSharpExtensionSelectionEvents.PublishRestoreAsync(agent, initial, initial, CancellationToken.None);
        await PiSharpExtensionSelectionEvents.PublishRestoreAsync(agent, initial, restored, CancellationToken.None);

        var eventData = Assert.Single(observed);
        Assert.Equal(PiSharpExtensionModelSelectionSource.Restore, eventData.Source);
        Assert.Equal("saved", eventData.Model.ModelId);
        Assert.Equal("default", eventData.PreviousModel?.ModelId);
    }

    private static ModelSelection Selection(string providerId, string modelId, string name)
    {
        var model = new ModelDescriptor(modelId, providerId, 32_000, "configured", Reasoning: true,
            Provider: providerId, Name: name);
        var provider = new ProviderProfile(providerId, providerId, new Uri("https://fixture.test/v1"),
            AuthRequired: false, OAuthSupported: false, ApiKeyEnvironment: null, ConfiguredApiKey: null, Models: [model]);
        return new ModelSelection(provider, model, "fixture-key", Authenticated: true, AuthSource: "test");
    }

    private sealed class StubClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "unused")]));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
