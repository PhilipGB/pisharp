using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using Microsoft.Extensions.AI;

namespace PiSharp.Tests;

public sealed class NativeExtensionEventRegistrationTests
{
    [Fact]
    public void TurnEndPayloadReturnsDetachedMessageCopies()
    {
        var original = new ChatMessage(ChatRole.Assistant, [new TextContent("finished")])
        {
            RawRepresentation = new object()
        };
        var eventData = new PiSharpExtensionTurnEndEvent(3, original, [], "message-3", [], false);
        var firstRead = eventData.Message!;
        firstRead.Contents.OfType<TextContent>().Single().Text = "callback mutation";

        var secondRead = eventData.Message!;
        Assert.Equal("finished", secondRead.Text);
        Assert.Null(secondRead.RawRepresentation);
        Assert.Equal("finished", original.Text);
    }

    [Fact]
    public void EventHandlersFollowCheckpointSourceReplacementRemovalAndLoadOrder()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var restore = registration.CreateConfigurationCheckpoint();
        registration.SetCurrentSourceInfo(Source("/extensions/temporary.dll"));
        RegisterAll(adapter);
        restore();
        Assert.All(EventHandlerSources(registration), handlers => Assert.Empty(handlers));

        var first = Source("/extensions/first.dll");
        var second = Source("/extensions/second.dll");
        registration.SetCurrentSourceInfo(first);
        RegisterAll(adapter);
        registration.SetCurrentSourceInfo(second);
        RegisterAll(adapter);

        registration.OrderExtensions([second, first]);
        Assert.All(EventHandlerSources(registration), handlers =>
            Assert.Equal([second, first], handlers));

        var replacement = second with { Source = "native-package-v2" };
        registration.ReconcileExtension(second, replacement);
        Assert.All(EventHandlerSources(registration), handlers =>
            Assert.Equal([replacement, first], handlers));

        registration.ReconcileExtension(replacement, selected: null);
        registration.ReconcileExtension(first, selected: null);
        Assert.All(EventHandlerSources(registration), handlers => Assert.Empty(handlers));
    }

    private static void RegisterAll(NativeExtensionRegistrationAdapter adapter)
    {
        adapter.AddSessionInfoChangedHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddSessionCompactHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddSessionCompactFailedHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddAgentStartHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddAgentEndHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddTurnStartHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddTurnEndHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddToolExecutionStartHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddToolExecutionUpdateHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddToolExecutionEndHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddModelSelectHandler((_, _, _) => ValueTask.CompletedTask);
        adapter.AddThinkingLevelSelectHandler((_, _, _) => ValueTask.CompletedTask);
    }

    private static IEnumerable<IEnumerable<ResourceSourceInfo>> EventHandlerSources(ExtensionRegistration registration) =>
    [
        registration.SessionInfoChangedHandlers.Select(entry => entry.SourceInfo),
        registration.SessionCompactHandlers.Select(entry => entry.SourceInfo),
        registration.SessionCompactFailedHandlers.Select(entry => entry.SourceInfo),
        registration.AgentStartHandlers.Select(entry => entry.SourceInfo),
        registration.AgentEndHandlers.Select(entry => entry.SourceInfo),
        registration.TurnStartHandlers.Select(entry => entry.SourceInfo),
        registration.TurnEndHandlers.Select(entry => entry.SourceInfo),
        registration.ToolExecutionStartHandlers.Select(entry => entry.SourceInfo),
        registration.ToolExecutionUpdateHandlers.Select(entry => entry.SourceInfo),
        registration.ToolExecutionEndHandlers.Select(entry => entry.SourceInfo),
        registration.ModelSelectHandlers.Select(entry => entry.SourceInfo),
        registration.ThinkingLevelSelectHandlers.Select(entry => entry.SourceInfo)
    ];

    private static ResourceSourceInfo Source(string path) =>
        new(path, "native-package", "user", "package", "/extensions");
}
