using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class NativeExtensionBoundaryDispatcherTests
{
    [Fact]
    public async Task BoundaryHandlersAreOrderedAndLaterReplacementWinsWhileOmittedFieldsRemain()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var first = Source("/extensions/first.dll");
        var second = Source("/extensions/second.dll");
        var third = Source("/extensions/third.dll");
        var observations = new List<(string Name, string[] Entries, bool Continue, string[] Preview)>();

        registration.SetCurrentSourceInfo(third);
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            observations.Add(("third", DraftNames(eventData.Entries), eventData.Continue,
                eventData.Context.ContextEntries.Select(entry => entry.Type).ToArray()));
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        registration.SetCurrentSourceInfo(first);
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            observations.Add(("first", DraftNames(eventData.Entries), eventData.Continue,
                eventData.Context.ContextEntries.Select(entry => entry.Type).ToArray()));
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("first")], @continue: true));
        });
        registration.SetCurrentSourceInfo(second);
        adapter.AddTurnEndBoundaryHandler((eventData, _, _) =>
        {
            observations.Add(("second", DraftNames(eventData.Entries), eventData.Continue,
                eventData.Context.ContextEntries.Select(entry => entry.Type).ToArray()));
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("second")], @continue: false));
        });
        adapter.Close();
        registration.OrderExtensions([first, second, third]);

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var decision = await dispatcher.DispatchTurnEndBoundaryAsync(Turn(), PiSharpExtensionAgentOutcome.Completed,
            BuildPreview, CancellationToken.None, diagnostics.Add);

        Assert.Equal(["first", "second", "third"], observations.Select(item => item.Name));
        Assert.Equal(Array.Empty<string>(), observations[0].Entries);
        Assert.False(observations[0].Continue);
        Assert.Empty(observations[0].Preview);
        Assert.Equal(["first"], observations[1].Entries);
        Assert.True(observations[1].Continue);
        Assert.Equal(["custom:first"], observations[1].Preview);
        Assert.Equal(["second"], observations[2].Entries);
        Assert.False(observations[2].Continue);
        Assert.Equal(["custom:second"], observations[2].Preview);
        Assert.True(decision.Valid);
        Assert.False(decision.Continue);
        Assert.Equal(["second"], DraftNames(decision.Entries));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InvalidIntermediateDraftKeepsLastValidPreviewUntilLaterHandlerRepairsIt()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var first = Source("/extensions/first.dll");
        var invalid = Source("/extensions/invalid.dll");
        var repair = Source("/extensions/repair.dll");
        string[]? downstreamDrafts = null;
        string[]? downstreamPreview = null;

        registration.SetCurrentSourceInfo(first);
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("last-good")], @continue: true)));
        registration.SetCurrentSourceInfo(invalid);
        adapter.AddAgentBeforeSettleHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("invalid")])));
        registration.SetCurrentSourceInfo(repair);
        adapter.AddAgentBeforeSettleHandler((eventData, _, _) =>
        {
            downstreamDrafts = DraftNames(eventData.Entries);
            downstreamPreview = eventData.Context.ContextEntries.Select(entry => entry.Type).ToArray();
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("repaired")]));
        });
        adapter.Close();
        registration.OrderExtensions([first, invalid, repair]);

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var decision = await dispatcher.DispatchAgentBeforeSettleAsync(PiSharpExtensionAgentOutcome.Completed,
            BuildPreview, CancellationToken.None, diagnostics.Add);

        Assert.Equal(["invalid"], downstreamDrafts!);
        Assert.Equal(["custom:last-good"], downstreamPreview!);
        Assert.True(decision.Valid);
        Assert.True(decision.Continue);
        Assert.Equal(["repaired"], DraftNames(decision.Entries));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("Invalid boundary entries:", diagnostic.Error);
        Assert.Equal(invalid.Path, ExtensionPath(diagnostic));
    }

    [Fact]
    public async Task InvalidFinalDraftRejectsEntriesAndContinuationWithSourceDiagnostic()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var source = Source("/extensions/invalid-final.dll");
        registration.SetCurrentSourceInfo(source);
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
            ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("invalid")], @continue: true)));
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var decision = await dispatcher.DispatchTurnEndBoundaryAsync(Turn(), PiSharpExtensionAgentOutcome.Error,
            BuildPreview, CancellationToken.None, diagnostics.Add);

        Assert.False(decision.Valid);
        Assert.False(decision.Continue);
        Assert.Empty(decision.Entries);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("Invalid boundary entries:", diagnostic.Error);
        Assert.Equal(source.Path, ExtensionPath(diagnostic));
    }

    [Fact]
    public async Task HandlerExceptionIsReportedAndLaterBoundaryHandlerStillRuns()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var broken = Source("/extensions/broken.dll");
        var next = Source("/extensions/next.dll");
        var invoked = new List<string>();
        registration.SetCurrentSourceInfo(broken);
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
        {
            invoked.Add("broken");
            throw new InvalidOperationException("boundary failed");
        });
        registration.SetCurrentSourceInfo(next);
        adapter.AddTurnEndBoundaryHandler((_, _, _) =>
        {
            invoked.Add("next");
            return ValueTask.FromResult<PiSharpBoundaryResult?>(new(
                [new PiSharpCustomEntryDraft("after-error")]));
        });
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var decision = await dispatcher.DispatchTurnEndBoundaryAsync(Turn(), PiSharpExtensionAgentOutcome.Completed,
            BuildPreview, CancellationToken.None, diagnostics.Add);

        Assert.Equal(["broken", "next"], invoked);
        Assert.True(decision.Valid);
        Assert.Equal(["after-error"], DraftNames(decision.Entries));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("boundary failed", diagnostic.Error);
        Assert.Equal(broken.Path, ExtensionPath(diagnostic));
    }

    [Fact]
    public async Task CancellationRevokesCallbackContextAndReturnsWhileHandlerIgnoresToken()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var source = Source("/extensions/blocked.dll");
        var callbackStarted = new TaskCompletionSource<PiSharp.Extensions.Abstractions.PiSharpExtensionContext>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishHandler = new TaskCompletionSource<PiSharpBoundaryResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken receivedToken = default;

        registration.SetCurrentSourceInfo(source);
        adapter.AddTurnEndBoundaryHandler(async (_, context, cancellationToken) =>
        {
            receivedToken = cancellationToken;
            callbackStarted.TrySetResult(context);
            return await finishHandler.Task;
        });
        adapter.Close();

        var runtimeContext = CreateContext();
        runtimeContext.SetSessionContextFactory(_ => new PiSharpExtensionSessionContext(
            () => new("session", null, null, []),
            (_, _, _) => throw new InvalidOperationException("A revoked session context must not write.")));
        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, runtimeContext, diagnostics.Add);
        using var cancellation = new CancellationTokenSource();
        var dispatch = dispatcher.DispatchTurnEndBoundaryAsync(Turn(), PiSharpExtensionAgentOutcome.Completed,
            BuildPreview, cancellation.Token, diagnostics.Add);
        var callbackContext = await callbackStarted.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        Assert.True(receivedToken.IsCancellationRequested);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => callbackContext.Session!.AppendEntryAsync("revoked"));
        Assert.Empty(diagnostics);

        finishHandler.TrySetResult(null);
    }

    [Fact]
    public async Task PreSettleAndSettledHandlersUseTheirRegisteredEventSurfaces()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var observedOutcome = PiSharpExtensionAgentOutcome.Completed;
        var observedAborted = false;
        registration.SetCurrentSourceInfo(Source("/extensions/lifecycle.dll"));
        adapter.AddAgentBeforeSettleHandler((eventData, _, _) =>
        {
            observedOutcome = eventData.Outcome;
            return ValueTask.FromResult<PiSharpBoundaryResult?>(null);
        });
        adapter.AddAgentSettledHandler((eventData, _, _) =>
        {
            observedAborted = eventData.Aborted;
            return ValueTask.CompletedTask;
        });
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        Assert.True(dispatcher.HasAgentBeforeSettleHandlers);
        Assert.False(dispatcher.HasTurnEndBoundaryHandlers);
        var boundary = await dispatcher.DispatchAgentBeforeSettleAsync(PiSharpExtensionAgentOutcome.Aborted,
            BuildPreview, CancellationToken.None, diagnostics.Add);
        await dispatcher.DispatchAgentSettledAsync(new(Aborted: true), CancellationToken.None, diagnostics.Add);

        Assert.True(boundary.Valid);
        Assert.Equal(PiSharpExtensionAgentOutcome.Aborted, observedOutcome);
        Assert.True(observedAborted);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BoundaryDraftConstructorsRejectContentKindsTheCanonicalCodecCannotStore()
    {
        Assert.Throws<ArgumentException>(() => new PiSharpCustomMessageEntryDraft("custom",
            (IEnumerable<AIContent>)[new FunctionCallContent("call-1", "read", new Dictionary<string, object?>())],
            display: false));
        Assert.Throws<ArgumentException>(() => new PiSharpCustomMessageEntryDraft("custom",
            (IEnumerable<AIContent>)[new DataContent(new byte[] { 1 }, "text/plain")], display: false));
        Assert.Throws<ArgumentException>(() => new PiSharpContextEditEntryDraft("entry-1",
            [new FunctionResultContent("call-1", "tool output")]));

        var callDraft = new PiSharpContextEditEntryDraft("assistant-entry",
            [new FunctionCallContent("call-2", "read", new Dictionary<string, object?>())]);
        Assert.IsType<FunctionCallContent>(Assert.Single(callDraft.Replacement!));
    }

    private static string[] DraftNames(IEnumerable<PiSharpSessionBoundaryDraft> drafts) => drafts
        .Cast<PiSharpCustomEntryDraft>().Select(draft => draft.CustomType).ToArray();

    private static PiSharpBoundaryContextPreview BuildPreview(
        IReadOnlyList<PiSharpSessionBoundaryDraft> drafts)
    {
        if (drafts.OfType<PiSharpCustomEntryDraft>().Any(draft => draft.CustomType == "invalid"))
            throw new InvalidDataException("target entry does not exist");
        var entries = drafts.OfType<PiSharpCustomEntryDraft>().Select((draft, index) =>
            new PiSharpBoundaryContextEntry($"entry-{index}", $"custom:{draft.CustomType}",
                JsonSerializer.SerializeToElement(new { customType = draft.CustomType })));
        return new(entries, [], [], canContinue: drafts.Count > 0);
    }

    private static PiSharpExtensionTurnEndEvent Turn() => new(0,
        new ChatMessage(ChatRole.Assistant, [new TextContent("answer")]), [], "assistant-entry", [], aborted: false);

    private static string? ExtensionPath(AgentLifecycleEvent diagnostic) =>
        diagnostic.Details?.GetType().GetProperty("extensionPath")?.GetValue(diagnostic.Details) as string;

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateContext() => new(
        new PiSharpExtensionContextConfiguration("/tmp", "test", HasUserInterface: false,
            IsProjectTrusted: true, new TestUi()), static () => null, static () => [], static () => true);

    private static ResourceSourceInfo Source(string path) => new(path, "native-package", "user", "package", null);

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
}
