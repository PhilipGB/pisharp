using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class NativeExtensionBoundaryRegistrationTests
{
    [Fact]
    public void BoundaryDraftsAndPreviewReturnDetachedSnapshots()
    {
        using var document = JsonDocument.Parse("{\"value\":1}");
        var custom = new PiSharpCustomEntryDraft("fixture", document.RootElement);
        var customMessage = new PiSharpCustomMessageEntryDraft("notice", "original", display: true,
            document.RootElement);
        var contextEdit = PiSharpContextEditEntryDraft.FromText("entry-1", "replacement");
        var compaction = new PiSharpCompactionEntryDraft("summary", firstKeptEntryId: null,
            details: document.RootElement);
        var message = new ChatMessage(ChatRole.User, [new TextContent("context")]);
        var entries = new List<PiSharpSessionBoundaryDraft> { custom, customMessage, contextEdit, compaction };
        var result = new PiSharpBoundaryResult(entries, @continue: true);
        var preview = new PiSharpBoundaryContextPreview(
            [new PiSharpBoundaryContextEntry("entry-1", "message", document.RootElement)],
            [message], [message], canContinue: true);
        var turn = new PiSharpExtensionTurnEndEvent(0, message, [], "entry-1", [], aborted: false);
        var turnBoundary = new PiSharpExtensionTurnEndBoundaryEvent(turn, PiSharpExtensionAgentOutcome.Completed,
            entries, @continue: true, preview);
        var settleBoundary = new PiSharpExtensionAgentBeforeSettleEvent(PiSharpExtensionAgentOutcome.Completed,
            entries, @continue: false, preview);

        entries.Clear();
        message.Contents.OfType<TextContent>().Single().Text = "mutated source";
        custom.Data!.Value.TryGetProperty("value", out var originalCustomValue);
        customMessage.Content[0].ToString();
        ((TextContent)customMessage.Content[0]).Text = "mutated snapshot";
        ((TextContent)customMessage.Content[0]).Text = "another mutation";
        ((TextContent)preview.ContextMessages[0].Contents[0]).Text = "mutated preview";

        Assert.Equal(4, result.Entries!.Count);
        Assert.Equal(4, turnBoundary.Entries.Count);
        Assert.Equal(4, settleBoundary.Entries.Count);
        Assert.Equal("1", originalCustomValue.GetRawText());
        Assert.Equal("original", ((TextContent)customMessage.Content[0]).Text);
        Assert.Equal("context", preview.ContextMessages[0].Text);
        Assert.Equal("context", preview.PendingMessages[0].Text);
        Assert.True(preview.CanContinue);
        Assert.Equal("entry-1", Assert.Single(preview.ContextEntries).Id);
        Assert.Null(compaction.FirstKeptEntryId);
        Assert.Null(new PiSharpContextEditEntryDraft("entry-1", replacement: null).Replacement);
    }

    [Fact]
    public void BoundaryHandlersParticipateInCheckpointOrderingReplacementRemovalAndCatalogRelease()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var checkpoint = registration.CreateConfigurationCheckpoint();
        registration.SetCurrentSourceInfo(Source("/extensions/temporary.dll"));
        RegisterAll(adapter);
        checkpoint();
        Assert.All(HandlerSources(registration), handlers => Assert.Empty(handlers));

        var first = Source("/extensions/first.dll");
        var second = Source("/extensions/second.dll");
        registration.SetCurrentSourceInfo(first);
        RegisterAll(adapter);
        registration.SetCurrentSourceInfo(second);
        RegisterAll(adapter);

        registration.OrderExtensions([second, first]);
        Assert.All(HandlerSources(registration), handlers => Assert.Equal([second, first], handlers));

        var replacement = second with { Source = "native-package-v2" };
        registration.ReconcileExtension(second, replacement);
        Assert.All(HandlerSources(registration), handlers => Assert.Equal([replacement, first], handlers));

        registration.ReconcileExtension(replacement, selected: null);
        registration.ReconcileExtension(first, selected: null);
        Assert.All(HandlerSources(registration), handlers => Assert.Empty(handlers));

        registration.SetCurrentSourceInfo(first);
        RegisterAll(adapter);
        registration.ReleaseExtensionReferences();
        Assert.All(HandlerSources(registration), handlers => Assert.Empty(handlers));
    }

    private static void RegisterAll(NativeExtensionRegistrationAdapter adapter)
    {
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult()));
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new PiSharpBoundaryResult()));
        adapter.AddAgentSettledHandler((_, _, _) => ValueTask.CompletedTask);
    }

    private static IEnumerable<IEnumerable<ResourceSourceInfo>> HandlerSources(ExtensionRegistration registration) =>
    [
        registration.TurnEndBoundaryHandlers.Select(entry => entry.SourceInfo),
        registration.AgentBeforeSettleHandlers.Select(entry => entry.SourceInfo),
        registration.AgentSettledHandlers.Select(entry => entry.SourceInfo)
    ];

    private static ResourceSourceInfo Source(string path) =>
        new(path, "native-package", "user", "package", "/extensions");
}
