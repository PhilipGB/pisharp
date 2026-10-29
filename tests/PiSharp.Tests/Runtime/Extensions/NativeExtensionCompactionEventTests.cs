using System.Runtime.CompilerServices;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class NativeExtensionCompactionEventTests
{
    [Fact]
    public async Task ManualCompactionPublishesDetachedEntryAfterPersistenceBeforeCompactionEnd()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compact-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<string>();
            var lifecycle = new List<string>();
            PiSharpExtensionSessionCompactEvent? compacted = null;
            string? changedSessionName = null;
            var canCompactDuringCallback = true;
            ConversationRun? run = null;
            adapter.AddSessionInfoChangedHandler((eventData, _, _) =>
            {
                changedSessionName = eventData.Name;
                return ValueTask.CompletedTask;
            });
            adapter.AddSessionCompactHandler(async (eventData, _, cancellationToken) =>
            {
                compacted = eventData;
                observed.Add("session_compact");
                canCompactDuringCallback = run!.CanCompactFromExtensionCallback;
                await run.SetSessionNameAsync("after compaction", cancellationToken);
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            var agent = new PiAgent(new SummaryClient(), new CodingTools(workingDirectory),
                liveExtensionRegistration: registration);
            run = await ConversationRun.OpenAsync(agent, conversation);
            var result = await run.CompactWithResultAsync(cancellationToken: CancellationToken.None,
                publishEvent: item =>
                {
                    if (item.Type == "compaction_end") lifecycle.Add("compaction_end");
                    return Task.CompletedTask;
                });

            Assert.NotNull(result);
            var entry = Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.NotNull(compacted);
            Assert.Equal(["session_compact"], observed);
            Assert.Equal(["compaction_end"], lifecycle);
            Assert.Equal(entry.Id, compacted.EntryId);
            Assert.Equal("after compaction", conversation.Name);
            Assert.Equal("after compaction", changedSessionName);
            Assert.False(canCompactDuringCallback);
            Assert.Equal(entry.Timestamp, compacted.Timestamp);
            Assert.Equal(PiSharpExtensionCompactionReason.Manual, compacted.Reason);
            Assert.False(compacted.FromExtension);
            Assert.False(compacted.WillRetry);
            Assert.Equal(result.Summary, compacted.Result.Summary);
            Assert.Equal(result.FirstKeptEntryId, compacted.Result.FirstKeptEntryId);
            Assert.Equal(result.TokensBefore, compacted.Result.TokensBefore);
            Assert.Equal(result.EstimatedTokensAfter, compacted.Result.EstimatedTokensAfter);
            Assert.Equal(JsonValueKind.Object, compacted.Result.Details?.ValueKind);
            Assert.Null(compacted.Result.Usage);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task ManualCompactionAfterHookObservesDurableEntry()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compact-durable-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var store = new ConversationStore(workingDirectory, Path.Combine(workingDirectory, "sessions"));
            var conversation = Seed(workingDirectory);
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);

            string? persistedEntryId = null;
            string? publishedEntryId = null;
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            adapter.AddSessionCompactHandler(async (eventData, _, cancellationToken) =>
            {
                publishedEntryId = eventData.EntryId;
                var persisted = await store.LoadAsync(path, cancellationToken);
                persistedEntryId = persisted.Tree.ActivePath()
                    .SingleOrDefault(entry => entry.Type == "compaction")?.Id;
            });
            adapter.Close();

            var run = await ConversationRun.OpenAsync(new PiAgent(new SummaryClient(), new CodingTools(workingDirectory),
                    liveExtensionRegistration: registration), conversation,
                save: token => store.SaveAsync(conversation, path, token), sessionFile: path);

            await run.CompactWithResultAsync(cancellationToken: CancellationToken.None);

            Assert.NotNull(publishedEntryId);
            Assert.Equal(publishedEntryId, persistedEntryId);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task ManualCompactionUsesAndPersistsValidatedExtensionSummary()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-compact-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var summaryRequests = 0;
            PiSharpExtensionSessionBeforeCompactEvent? beforeCompact = null;
            PiSharpExtensionSessionCompactEvent? compacted = null;
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                beforeCompact = eventData;
                var retainedEntryId = eventData.BranchEntries[1].Id;
                var custom = new PiSharpExtensionCustomCompaction("Extension checkpoint", retainedEntryId, 123,
                    JsonSerializer.SerializeToElement(new { format = "custom", revision = 2 }),
                    new PiSharpExtensionCompactionUsage(100, 25, CachedInputTokens: 10,
                        ReasoningTokens: 5, TotalTokens: 125, CachedWriteTokens: 3));
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction: custom));
            });
            adapter.AddSessionCompactHandler((eventData, _, _) =>
            {
                compacted = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            string? persisted = null;
            var agent = new PiAgent(new SummaryClient(onRequest: () => summaryRequests++),
                new CodingTools(workingDirectory), liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation,
                save: _ =>
                {
                    persisted = conversation.ToJson();
                    return Task.CompletedTask;
                });

            var result = await run.CompactWithResultAsync("custom focus", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(0, summaryRequests);
            Assert.NotNull(beforeCompact);
            Assert.Equal(PiSharpExtensionCompactionReason.Manual, beforeCompact.Reason);
            Assert.False(beforeCompact.WillRetry);
            Assert.Equal("custom focus", beforeCompact.CustomInstructions);
            Assert.Equal(beforeCompact.BranchEntries[1].Id, result.FirstKeptEntryId);
            Assert.NotEqual(beforeCompact.Preparation.FirstKeptEntryId, result.FirstKeptEntryId);
            Assert.NotNull(compacted);
            Assert.True(compacted.FromExtension);
            Assert.Equal("Extension checkpoint", compacted.Result.Summary);
            Assert.Equal(123, compacted.Result.TokensBefore);
            Assert.Equal(JsonValueKind.Object, compacted.Result.Details?.ValueKind);
            Assert.Equal("custom", compacted.Result.Details?.GetProperty("format").GetString());
            Assert.Equal(100, result.Usage?.InputTokens);
            Assert.Equal(25, result.Usage?.OutputTokens);
            var persistedConversation = ConversationSession.Parse(Assert.IsType<string>(persisted));
            var compaction = Assert.Single(persistedConversation.Tree.ActivePath(), entry => entry.Type == "compaction");
            Assert.True(compaction.Payload.GetProperty("fromHook").GetBoolean());
            Assert.Equal("custom", compaction.Payload.GetProperty("details").GetProperty("format").GetString());
            var usage = Assert.Single(persistedConversation.ActiveUsage(), item => item.Source == "compaction");
            Assert.Equal(100, usage.InputTokens);
            Assert.Equal(25, usage.OutputTokens);
            Assert.Equal(3, usage.CachedWriteTokens);

            var resumedRequests = new List<IReadOnlyList<ChatMessage>>();
            var resumedAgent = new PiAgent(new SummaryClient(response: "continued after resume",
                    onMessages: messages => resumedRequests.Add(messages)),
                new CodingTools(workingDirectory), noBuiltinTools: true);
            var resumedRun = await ConversationRun.OpenAsync(resumedAgent, persistedConversation);
            await foreach (var _ in resumedRun.RunEventsAsync("continue after custom compaction")) { }
            Assert.Contains(Assert.Single(resumedRequests), message =>
                message.Text?.Contains("Extension checkpoint", StringComparison.Ordinal) == true);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task InvalidCustomCompactionIsAttributedAndLeavesCanonicalSessionUnchanged()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-invalid-custom-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var source = new PiSharp.Runtime.Resources.ResourceSourceInfo(
                "/extensions/invalid-compact.dll", "invalid-compact", "user", "package", null);
            registration.SetCurrentSourceInfo(source);
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
                ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction:
                    new PiSharpExtensionCustomCompaction("invalid summary", "stale-entry", 123))));
            PiSharpExtensionSessionCompactFailedEvent? failed = null;
            adapter.AddSessionCompactFailedHandler((eventData, _, _) =>
            {
                failed = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            var original = conversation.ToJson();
            var summaryRequests = 0;
            var saves = 0;
            var run = await ConversationRun.OpenAsync(new PiAgent(
                    new SummaryClient(onRequest: () => summaryRequests++), new CodingTools(workingDirectory),
                    liveExtensionRegistration: registration), conversation,
                save: _ =>
                {
                    saves++;
                    return Task.CompletedTask;
                });

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                run.CompactWithResultAsync(cancellationToken: CancellationToken.None));

            Assert.Contains(source.Path, error.Message, StringComparison.Ordinal);
            Assert.Contains("active branch", error.Message, StringComparison.Ordinal);
            Assert.Equal(original, conversation.ToJson());
            Assert.Equal(0, summaryRequests);
            Assert.Equal(0, saves);
            Assert.NotNull(failed);
            Assert.True(failed.FromExtension);
            Assert.Contains(source.Path, failed.ErrorMessage, StringComparison.Ordinal);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task CustomCompactionCannotSplitAnIncompleteToolCallResultPair()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-invalid-tool-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, "read the notes"));
            conversation.Append(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("read-call", "read", new Dictionary<string, object?> { ["path"] = "notes.md" })]));
            var toolCallEntry = conversation.Tree.HeadId!;
            conversation.Append(new ChatMessage(ChatRole.User, "next question"));
            var retainedEntry = conversation.Tree.HeadId!;
            conversation.Append(new ChatMessage(ChatRole.Assistant, "next answer"));

            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                Assert.Contains(eventData.BranchEntries, entry => entry.Id == toolCallEntry);
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction:
                    new PiSharpExtensionCustomCompaction("invalid tool boundary", retainedEntry,
                        eventData.Preparation.TokensBefore)));
            });
            adapter.Close();

            var original = conversation.ToJson();
            var summaryRequests = 0;
            var run = await ConversationRun.OpenAsync(new PiAgent(
                    new SummaryClient(onRequest: () => summaryRequests++), new CodingTools(workingDirectory),
                    liveExtensionRegistration: registration), conversation, keepRecentTokens: 1);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                run.CompactWithResultAsync(cancellationToken: CancellationToken.None));

            Assert.Contains("incomplete tool call/result pair", error.Message, StringComparison.Ordinal);
            Assert.Equal(original, conversation.ToJson());
            Assert.Equal(0, summaryRequests);
            Assert.DoesNotContain(conversation.Tree.ActivePath(), entry => entry.Type == "compaction");
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task ManualCompactionCanBeCancelledByExtensionWithoutChangingConversationOrHoldingGate()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-cancel-custom-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var beforeCompactCalls = 0;
            PiSharpExtensionSessionCompactFailedEvent? failed = null;
            adapter.AddSessionBeforeCompactHandler((_, _, _) =>
            {
                beforeCompactCalls++;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(
                    beforeCompactCalls == 1 ? new(Cancel: true) : null);
            });
            adapter.AddSessionCompactFailedHandler((eventData, _, _) =>
            {
                failed = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            var originalHead = conversation.Tree.HeadId;
            var summaryRequests = 0;
            var agent = new PiAgent(new SummaryClient(onRequest: () => summaryRequests++), new CodingTools(workingDirectory),
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                run.CompactWithResultAsync(cancellationToken: CancellationToken.None));

            Assert.Equal("Compaction cancelled by extension.", error.Message);
            Assert.Equal(originalHead, conversation.Tree.HeadId);
            Assert.NotNull(failed);
            Assert.True(failed.Aborted);
            Assert.Null(failed.ErrorMessage);
            Assert.False(failed.FromExtension);
            Assert.DoesNotContain(conversation.Tree.ActivePath(), node => node.Type == "compaction");

            var captureDirectory = Environment.GetEnvironmentVariable("PISHARP_C1_MANUAL_CANCEL_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                var capture = new
                {
                    scenario = "manual-cancel",
                    compactionCount = conversation.Tree.ActivePath().Count(node => node.Type == "compaction"),
                    canonicalStateUnchanged = conversation.Tree.HeadId == originalHead,
                    summaryRequestCount = summaryRequests,
                    abortedEventPublished = failed.Aborted,
                    failureError = failed.ErrorMessage,
                    directError = error.Message,
                };
                await File.WriteAllTextAsync(Path.Combine(captureDirectory, "manual-cancel.json"),
                    JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }

            var nextResult = await run.CompactWithResultAsync(cancellationToken: CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(nextResult);
            Assert.Equal(2, beforeCompactCalls);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task ManualCompactionFailurePublishesFailureAfterCompactionEnd()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compact-fail-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<string>();
            var lifecycle = new List<string>();
            PiSharpExtensionSessionCompactFailedEvent? failed = null;
            adapter.AddSessionCompactFailedHandler((eventData, _, _) =>
            {
                failed = eventData;
                observed.Add("session_compact_failed");
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            var head = conversation.Tree.HeadId;
            var agent = new PiAgent(new SummaryClient(fail: true), new CodingTools(workingDirectory),
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await Assert.ThrowsAsync<IOException>(() => run.CompactWithResultAsync(
                cancellationToken: CancellationToken.None,
                publishEvent: item =>
                {
                    if (item.Type == "compaction_end") lifecycle.Add("compaction_end");
                    return Task.CompletedTask;
                }));

            Assert.Equal(["session_compact_failed"], observed);
            Assert.Equal(["compaction_end"], lifecycle);
            Assert.Equal(head, conversation.Tree.HeadId);
            Assert.NotNull(failed);
            Assert.Equal(PiSharpExtensionCompactionReason.Manual, failed.Reason);
            Assert.False(failed.WillRetry);
            Assert.False(failed.Aborted);
            Assert.Equal("Compaction failed: Summarization failed: summary failed", failed.ErrorMessage);
            Assert.False(failed.FromExtension);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task ManualCompactionWithoutBoundaryPublishesFailureAfterCompactionEnd()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compact-empty-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<string>();
            var lifecycle = new List<string>();
            PiSharpExtensionSessionCompactFailedEvent? failed = null;
            adapter.AddSessionCompactFailedHandler((eventData, _, _) =>
            {
                failed = eventData;
                observed.Add("session_compact_failed");
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = new ConversationSession(workingDirectory, "fixture", null);
            conversation.Append(new ChatMessage(ChatRole.User, "only one entry"));
            var agent = new PiAgent(new SummaryClient(), new CodingTools(workingDirectory),
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation);

            var result = await run.CompactWithResultAsync(cancellationToken: CancellationToken.None,
                publishEvent: item =>
                {
                    if (item.Type == "compaction_end") lifecycle.Add("compaction_end");
                    return Task.CompletedTask;
                });

            Assert.Null(result);
            Assert.Equal(["session_compact_failed"], observed);
            Assert.Equal(["compaction_end"], lifecycle);
            Assert.NotNull(failed);
            Assert.Equal(PiSharpExtensionCompactionReason.Manual, failed.Reason);
            Assert.False(failed.Aborted);
            Assert.Equal("Compaction failed: Nothing to compact (session too small)", failed.ErrorMessage);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task ManualCompactionAbortPublishesAbortedFailureAfterCompactionEnd()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compact-abort-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<string>();
            var lifecycle = new List<string>();
            PiSharpExtensionSessionCompactFailedEvent? failed = null;
            adapter.AddSessionCompactFailedHandler((eventData, _, _) =>
            {
                failed = eventData;
                observed.Add("session_compact_failed");
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            using var cancellation = new CancellationTokenSource();
            var conversation = Seed(workingDirectory);
            var agent = new PiAgent(new SummaryClient(onRequest: cancellation.Cancel), new CodingTools(workingDirectory),
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.CompactWithResultAsync(
                cancellationToken: cancellation.Token,
                publishEvent: item =>
                {
                    if (item.Type == "compaction_end") lifecycle.Add("compaction_end");
                    return Task.CompletedTask;
                }));

            Assert.Equal(["session_compact_failed"], observed);
            Assert.Equal(["compaction_end"], lifecycle);
            Assert.NotNull(failed);
            Assert.Equal(PiSharpExtensionCompactionReason.Manual, failed.Reason);
            Assert.True(failed.Aborted);
            Assert.Null(failed.ErrorMessage);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task ManualCompactionCancellationDoesNotWaitForFailureObserverOrHoldExecutionGate()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compact-observer-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var observerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharpExtensionSessionCompactFailedEvent? failed = null;
            PiSharp.Extensions.Abstractions.PiSharpExtensionContext? retainedContext = null;
            var observerToken = CancellationToken.None;
            adapter.AddSessionCompactFailedHandler((eventData, context, cancellationToken) =>
            {
                failed = eventData;
                retainedContext = context;
                observerToken = cancellationToken;
                observerStarted.TrySetResult();
                return new ValueTask(completeObserver.Task);
            });
            adapter.Close();

            using var cancellation = new CancellationTokenSource();
            var summaryRequests = 0;
            var summaryClient = new SummaryClient(onRequest: () =>
            {
                if (Interlocked.Increment(ref summaryRequests) == 1) cancellation.Cancel();
            });
            var conversation = Seed(workingDirectory);
            var agent = new PiAgent(summaryClient, new CodingTools(workingDirectory),
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation);
            var activeToolMutations = 0;
            var controlOperations = new PiSharpExtensionCommandControlOperations(
                static () => [], static () => [], static () => [], static () => null,
                _ => activeToolMutations++,
                static (_, _, _) => Task.FromResult(
                    new PiSharpModelSelectionResult(PiSharpModelSelectionStatus.Selected)),
                static () => PiSharpThinkingLevel.Off,
                static (level, _) => Task.FromResult(level),
                static () => { },
                static _ => Task.FromResult(new PiSharpCompactionResult(PiSharpCompactionStatus.NotNeeded)),
                static () => { });
            run.SetExtensionCapabilitiesFactory((_, session) =>
                new PiSharpExtensionCapabilities(controlOperations, session, null));

            var cancelledCompaction = run.CompactWithResultAsync(cancellationToken: cancellation.Token);
            await observerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cancelledCompaction.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.NotNull(failed);
            Assert.True(failed.Aborted);
            Assert.True(observerToken.IsCancellationRequested);
            Assert.False(completeObserver.Task.IsCompleted);
            Assert.NotNull(retainedContext);
            Assert.Throws<ObjectDisposedException>(() => retainedContext.Capabilities!.SetActiveTools(["late-tool"]));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await retainedContext.Session!.SetSessionNameAsync("late mutation"));
            Assert.Equal(0, activeToolMutations);

            var nextCompaction = await run.CompactWithResultAsync(cancellationToken: CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.NotNull(nextCompaction);
            Assert.Equal(2, summaryRequests);
        }
        finally
        {
            completeObserver.TrySetResult();
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledBeforeCompactHandlerLosesItsContextAndReleasesExecutionGate()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-before-compact-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeHandler = new TaskCompletionSource<PiSharpExtensionSessionBeforeCompactResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharp.Extensions.Abstractions.PiSharpExtensionContext? retainedContext = null;
            var handlerToken = CancellationToken.None;
            var calls = 0;
            adapter.AddSessionBeforeCompactHandler((_, context, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                    return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(null);
                retainedContext = context;
                handlerToken = cancellationToken;
                handlerStarted.TrySetResult();
                return new ValueTask<PiSharpExtensionSessionBeforeCompactResult?>(completeHandler.Task);
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            var summaryRequests = 0;
            var run = await ConversationRun.OpenAsync(new PiAgent(new SummaryClient(onRequest: () => summaryRequests++),
                new CodingTools(workingDirectory), liveExtensionRegistration: registration), conversation);
            var activeToolMutations = 0;
            var controls = new PiSharpExtensionCommandControlOperations(
                static () => [], static () => [], static () => [], static () => null,
                _ => activeToolMutations++,
                static (_, _, _) => Task.FromResult(
                    new PiSharpModelSelectionResult(PiSharpModelSelectionStatus.Selected)),
                static () => PiSharpThinkingLevel.Off,
                static (level, _) => Task.FromResult(level),
                static () => { },
                static _ => Task.FromResult(new PiSharpCompactionResult(PiSharpCompactionStatus.NotNeeded)),
                static () => { });
            run.SetExtensionCapabilitiesFactory((_, session) =>
                new PiSharpExtensionCapabilities(controls, session, null));

            using var cancellation = new CancellationTokenSource();
            var originalHead = conversation.Tree.HeadId;
            var blocked = run.CompactWithResultAsync(cancellationToken: cancellation.Token);
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(originalHead, conversation.Tree.HeadId);
            Assert.True(handlerToken.IsCancellationRequested);
            Assert.NotNull(retainedContext);
            Assert.Throws<ObjectDisposedException>(() => retainedContext.Capabilities!.SetActiveTools(["late-tool"]));
            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                await retainedContext.Session!.SetSessionNameAsync("late mutation"));
            Assert.Equal(0, activeToolMutations);

            Assert.NotNull(await run.CompactWithResultAsync(cancellationToken: CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, summaryRequests);
        }
        finally
        {
            completeHandler.TrySetResult(null);
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task VirtualRouteThresholdCompactionPublishesAfterPersistenceBeforeCompactionEnd()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compact-threshold-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<string>();
            var lifecycle = new List<string>();
            PiSharpExtensionSessionBeforeCompactEvent? beforeCompact = null;
            PiSharpExtensionSessionCompactEvent? compacted = null;
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                beforeCompact = eventData;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(null);
            });
            adapter.AddSessionCompactHandler((eventData, _, _) =>
            {
                compacted = eventData;
                observed.Add("session_compact");
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = new ConversationSession(workingDirectory, "auto", null, "router");
            conversation.SelectModel("auto", null, "router");
            conversation.Append(new ChatMessage(ChatRole.User, "old " + new string('x', 18000)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
            var inference = new SummaryClient(response: "physical answer");
            var summarizer = new SummaryClient(response: "Earlier context summary");
            var agent = new PiAgent(inference, new CodingTools(workingDirectory), noTools: true,
                liveExtensionRegistration: registration,
                virtualModelRequestRouter: request => Task.FromResult(new VirtualModelRequestRoute(
                    request.Reason == "direct" ? summarizer : inference,
                    new ModelDescriptor(request.Reason == "direct" ? "summary-model" : "physical", null, 5000, null,
                        Provider: "physical", Api: "test-api"), "physical", "off", null, null,
                    new AutoCompactionPolicy(5000, ReserveTokens: 0, KeepRecentTokens: 1))));
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await foreach (var item in run.RunEventsAsync("next"))
            {
                if (item.Type is "compaction_start" or "compaction_end") lifecycle.Add(item.Type);
            }

            var entry = Assert.Single(conversation.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.Equal(["session_compact"], observed);
            Assert.Equal(["compaction_start", "compaction_end"], lifecycle);
            Assert.NotNull(compacted);
            Assert.NotNull(beforeCompact);
            Assert.Equal(PiSharpExtensionCompactionReason.Threshold, beforeCompact.Reason);
            Assert.False(beforeCompact.WillRetry);
            Assert.Null(beforeCompact.CustomInstructions);
            Assert.True(beforeCompact.Preparation.MessagesToSummarize.Count > 0 ||
                beforeCompact.Preparation.TurnPrefixMessages.Count > 0);
            Assert.Equal(entry.Id, compacted.EntryId);
            Assert.Equal(PiSharpExtensionCompactionReason.Threshold, compacted.Reason);
            Assert.False(compacted.FromExtension);
            Assert.False(compacted.WillRetry);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task VirtualRouteOverflowCompactionPublishesRetryMetadata()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-compact-overflow-event-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var observed = new List<string>();
            var lifecycle = new List<string>();
            PiSharpExtensionSessionBeforeCompactEvent? beforeCompact = null;
            PiSharpExtensionSessionCompactEvent? compacted = null;
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                beforeCompact = eventData;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(null);
            });
            adapter.AddSessionCompactHandler((eventData, _, _) =>
            {
                compacted = eventData;
                observed.Add("session_compact");
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = new ConversationSession(workingDirectory, "auto", null, "router");
            conversation.SelectModel("auto", null, "router");
            conversation.Append(new ChatMessage(ChatRole.User, "old " + new string('x', 12000)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
            var failing = new SummaryClient(error: new HttpRequestException("context_length_exceeded", null,
                HttpStatusCode.BadRequest));
            var retry = new SummaryClient(response: "retry answer");
            var summarizer = new SummaryClient(response: "Earlier context summary");
            var agent = new PiAgent(failing, new CodingTools(workingDirectory), noTools: true,
                liveExtensionRegistration: registration,
                virtualModelRequestRouter: request => Task.FromResult(new VirtualModelRequestRoute(
                    request.Reason switch
                    {
                        "direct" => summarizer,
                        "retry" => retry,
                        _ => failing
                    },
                    new ModelDescriptor(request.Reason == "direct" ? "summary-model" : "physical", null, 50000, null,
                        Provider: "physical", Api: "test-api"), "physical", "off", null, null,
                    new AutoCompactionPolicy(50000, ReserveTokens: 0, KeepRecentTokens: 1))));
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await foreach (var item in run.RunEventsAsync("next"))
            {
                if (item.Type == "compaction_start") lifecycle.Add(item.Type);
                else if (item.Type == "compaction_end")
                {
                    lifecycle.Add(item.Type);
                    Assert.True(item.CompactionWillRetry);
                }
            }

            Assert.Equal(["session_compact"], observed);
            Assert.Equal(["compaction_start", "compaction_end"], lifecycle);
            Assert.NotNull(compacted);
            Assert.NotNull(beforeCompact);
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, beforeCompact.Reason);
            Assert.True(beforeCompact.WillRetry);
            Assert.Null(beforeCompact.CustomInstructions);
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, compacted.Reason);
            Assert.False(compacted.FromExtension);
            Assert.True(compacted.WillRetry);
            Assert.Equal("retry answer", conversation.ActiveMessages().Last().Text);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task VirtualRouteOverflowCompactionCanBeCancelledWithoutRetry()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-cancel-virtual-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var beforeEvents = new List<PiSharpExtensionSessionBeforeCompactEvent>();
            PiSharpExtensionSessionCompactFailedEvent? failed = null;
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                beforeEvents.Add(eventData);
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Cancel: true));
            });
            adapter.AddSessionCompactFailedHandler((eventData, _, _) =>
            {
                failed = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = new ConversationSession(workingDirectory, "auto", null, "router");
            conversation.SelectModel("auto", null, "router");
            conversation.Append(new ChatMessage(ChatRole.User, "old " + new string('x', 12000)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
            var failingRequests = 0;
            var retryRequests = 0;
            var summarizerRequests = 0;
            var failing = new SummaryClient(onRequest: () => failingRequests++, error:
                new HttpRequestException("context_length_exceeded", null, HttpStatusCode.BadRequest));
            var retry = new SummaryClient(onRequest: () => retryRequests++);
            var summarizer = new SummaryClient(onRequest: () => summarizerRequests++);
            var agent = new PiAgent(failing, new CodingTools(workingDirectory), noTools: true,
                liveExtensionRegistration: registration,
                virtualModelRequestRouter: request => Task.FromResult(new VirtualModelRequestRoute(
                    request.Reason switch
                    {
                        "direct" => summarizer,
                        "retry" => retry,
                        _ => failing
                    },
                    new ModelDescriptor("physical", null, 50000, null, Provider: "physical", Api: "test-api"),
                    "physical", "off", null, null,
                    new AutoCompactionPolicy(50000, ReserveTokens: 0, KeepRecentTokens: 1))));
            var run = await ConversationRun.OpenAsync(agent, conversation);

            await foreach (var _ in run.RunEventsAsync("next")) { }

            var before = Assert.Single(beforeEvents);
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, before.Reason);
            Assert.True(before.WillRetry);
            Assert.Equal(1, failingRequests);
            Assert.Equal(0, retryRequests);
            Assert.Equal(0, summarizerRequests);
            Assert.DoesNotContain(conversation.Tree.ActivePath(), entry => entry.Type == "compaction");
            Assert.NotNull(failed);
            Assert.True(failed.Aborted);
            Assert.False(failed.FromExtension);
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, failed.Reason);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Theory]
    [InlineData(false, PiSharpExtensionCompactionReason.Threshold)]
    [InlineData(true, PiSharpExtensionCompactionReason.Overflow)]
    public async Task AutomaticVirtualCompactionUsesValidatedCustomSummary(bool overflow,
        PiSharpExtensionCompactionReason expectedReason)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-automatic-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharpExtensionSessionBeforeCompactEvent? beforeCompact = null;
            PiSharpExtensionSessionCompactEvent? compacted = null;
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                beforeCompact = eventData;
                var custom = new PiSharpExtensionCustomCompaction("Extension automatic checkpoint",
                    eventData.Preparation.FirstKeptEntryId, 456,
                    JsonSerializer.SerializeToElement(new { source = "automatic", overflow }));
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction: custom));
            });
            adapter.AddSessionCompactHandler((eventData, _, _) =>
            {
                compacted = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var contextLength = overflow ? 50000 : 5000;
            var conversation = new ConversationSession(workingDirectory, "auto", null, "router");
            conversation.SelectModel("auto", null, "router");
            conversation.Append(new ChatMessage(ChatRole.User,
                "old " + new string('x', overflow ? 12000 : 18000)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "old answer"));

            var initialRequests = 0;
            var retryRequests = 0;
            var summaryRequests = 0;
            var initial = new SummaryClient(onRequest: () => initialRequests++, response: "physical answer",
                error: overflow
                    ? new HttpRequestException("context_length_exceeded", null, HttpStatusCode.BadRequest)
                    : null);
            var retry = new SummaryClient(onRequest: () => retryRequests++, response: "retry answer");
            var summarizer = new SummaryClient(onRequest: () => summaryRequests++, response: "default summary");
            var agent = new PiAgent(initial, new CodingTools(workingDirectory), noTools: true,
                liveExtensionRegistration: registration,
                virtualModelRequestRouter: request => Task.FromResult(new VirtualModelRequestRoute(
                    request.Reason switch
                    {
                        "direct" => summarizer,
                        "retry" => retry,
                        _ => initial
                    },
                    new ModelDescriptor(request.Reason == "direct" ? "summary-model" : "physical", null,
                        contextLength, null, Provider: "physical", Api: "test-api"),
                    "physical", "off", null, null,
                    new AutoCompactionPolicy(contextLength, ReserveTokens: 0, KeepRecentTokens: 1))));

            string? persisted = null;
            var run = await ConversationRun.OpenAsync(agent, conversation, save: _ =>
            {
                persisted = conversation.ToJson();
                return Task.CompletedTask;
            });
            await foreach (var _ in run.RunEventsAsync("next")) { }

            Assert.NotNull(beforeCompact);
            Assert.Equal(expectedReason, beforeCompact.Reason);
            Assert.Equal(overflow, beforeCompact.WillRetry);
            Assert.NotNull(compacted);
            Assert.Equal(expectedReason, compacted.Reason);
            Assert.True(compacted.FromExtension);
            Assert.Equal("Extension automatic checkpoint", compacted.Result.Summary);
            Assert.Equal(456, compacted.Result.TokensBefore);
            Assert.Equal(0, summaryRequests);
            Assert.True(initialRequests > 0);
            Assert.Equal(overflow ? 1 : 0, retryRequests);

            var saved = ConversationSession.Parse(Assert.IsType<string>(persisted));
            var entry = Assert.Single(saved.Tree.ActivePath(), node => node.Type == "compaction");
            Assert.True(entry.Payload.GetProperty("fromHook").GetBoolean());
            Assert.Equal("Extension automatic checkpoint", entry.Payload.GetProperty("summary").GetString());
            Assert.Equal(overflow.ToString().ToLowerInvariant(),
                entry.Payload.GetProperty("details").GetProperty("overflow").GetRawText());
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task PhysicalThresholdCompactionDispatchesBeforeCompactHook()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-physical-compact-threshold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharpExtensionSessionBeforeCompactEvent? beforeCompact = null;
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                beforeCompact = eventData;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(null);
            });
            adapter.Close();

            var conversation = new ConversationSession(workingDirectory, "physical", null);
            conversation.Append(new ChatMessage(ChatRole.User, new string('x', 6000)));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "earlier answer"));
            var agent = new PiAgent(new SummaryClient(), new CodingTools(workingDirectory), noTools: true,
                liveExtensionRegistration: registration);
            var run = await ConversationRun.OpenAsync(agent, conversation,
                autoCompaction: new AutoCompactionPolicy(1800, ReserveTokens: 0, KeepRecentTokens: 1));

            await foreach (var _ in run.RunEventsAsync("continue")) { }

            Assert.NotNull(beforeCompact);
            Assert.Equal(PiSharpExtensionCompactionReason.Threshold, beforeCompact.Reason);
            Assert.False(beforeCompact.WillRetry);
            Assert.Null(beforeCompact.CustomInstructions);
            Assert.True(beforeCompact.Preparation.MessagesToSummarize.Count > 0 ||
                beforeCompact.Preparation.TurnPrefixMessages.Count > 0);
            Assert.Contains(conversation.Tree.ActivePath(), node => node.Type == "compaction");
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task ThresholdCompactionCanBeCancelledWithoutChangingCanonicalHistoryOrHoldingGate()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-cancel-threshold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            var cancel = true;
            var beforeEvents = new List<PiSharpExtensionSessionBeforeCompactEvent>();
            var failedEvents = new List<PiSharpExtensionSessionCompactFailedEvent>();
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                beforeEvents.Add(eventData);
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(cancel
                    ? new(Cancel: true) : null);
            });
            adapter.AddSessionCompactFailedHandler((eventData, _, _) =>
            {
                failedEvents.Add(eventData);
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            var original = conversation.ToJson();
            var summaryRequests = 0;
            var client = new SummaryClient(onRequest: () => summaryRequests++);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(workingDirectory),
                    noBuiltinTools: true, liveExtensionRegistration: registration), conversation,
                autoCompaction: new AutoCompactionPolicy(10, ReserveTokens: 0, KeepRecentTokens: 1));

            await foreach (var _ in run.RunEventsAsync("continue")) { }

            Assert.Equal(PiSharpExtensionCompactionReason.Threshold, Assert.Single(beforeEvents).Reason);
            Assert.True(Assert.Single(failedEvents).Aborted);
            Assert.Equal(0, summaryRequests);
            Assert.DoesNotContain(conversation.Tree.ActivePath(), entry => entry.Type == "compaction");

            cancel = false;
            Assert.NotNull(await run.CompactWithResultAsync());
            Assert.True(summaryRequests > 0);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task PhysicalProviderOverflowCompactionCanBeCancelledBeforeSummarizationOrRetry()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-cancel-physical-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharpExtensionSessionBeforeCompactEvent? before = null;
            PiSharpExtensionSessionCompactFailedEvent? failed = null;
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                before = eventData;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Cancel: true));
            });
            adapter.AddSessionCompactFailedHandler((eventData, _, _) =>
            {
                failed = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            var requestCount = 0;
            var runEvents = new List<AgentLifecycleEvent>();
            var client = new SummaryClient(onRequest: () => requestCount++, error:
                new HttpRequestException("context_length_exceeded", null, HttpStatusCode.BadRequest));
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(workingDirectory),
                    noBuiltinTools: true, liveExtensionRegistration: registration), conversation,
                autoCompaction: new AutoCompactionPolicy(50000, ReserveTokens: 0, KeepRecentTokens: 1));
            Assert.True(run.HasSessionBeforeCompactHandlers);
            Assert.NotNull(ConversationCompactionCoordinator.PreparePlan(conversation, 1));

            await foreach (var item in run.RunEventsAsync("overflow request")) runEvents.Add(item);

            Assert.True(before is not null, $"requests={requestCount}; events={string.Join(",", runEvents.Select(item => item.Type + ":" + item.Error))}");
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, before.Reason);
            Assert.True(before.WillRetry);
            Assert.Equal(1, requestCount);
            Assert.DoesNotContain(conversation.Tree.ActivePath(), entry => entry.Type == "compaction");
            Assert.NotNull(failed);
            Assert.True(failed.Aborted);
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, failed.Reason);
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    [Fact]
    public async Task PhysicalProviderOverflowUsesCustomSummaryWithoutCallingDefaultSummarizer()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-custom-physical-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            PiSharpExtensionSessionBeforeCompactEvent? before = null;
            PiSharpExtensionSessionCompactEvent? compacted = null;
            adapter.AddSessionBeforeCompactHandler((eventData, _, _) =>
            {
                before = eventData;
                var custom = new PiSharpExtensionCustomCompaction("Physical overflow checkpoint",
                    eventData.Preparation.FirstKeptEntryId, 321);
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction: custom));
            });
            adapter.AddSessionCompactHandler((eventData, _, _) =>
            {
                compacted = eventData;
                return ValueTask.CompletedTask;
            });
            adapter.Close();

            var conversation = Seed(workingDirectory);
            var client = new OverflowThenSuccessClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(workingDirectory),
                    noBuiltinTools: true, liveExtensionRegistration: registration), conversation,
                autoCompaction: new AutoCompactionPolicy(50000, ReserveTokens: 0, KeepRecentTokens: 1));

            await foreach (var _ in run.RunEventsAsync("overflow request")) { }

            Assert.NotNull(before);
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, before.Reason);
            Assert.True(before.WillRetry);
            Assert.NotNull(compacted);
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, compacted.Reason);
            Assert.True(compacted.WillRetry);
            Assert.True(compacted.FromExtension);
            Assert.Equal("Physical overflow checkpoint", compacted.Result.Summary);
            Assert.Equal(2, client.RequestCount);
            Assert.Contains(conversation.Tree.ActivePath(), entry => entry.Type == "compaction" &&
                entry.Payload.GetProperty("fromHook").GetBoolean() &&
                entry.Payload.GetProperty("summary").GetString() == "Physical overflow checkpoint");
            Assert.Contains(conversation.ActiveMessages(), message => message.Text == "retried answer");
        }
        finally { Directory.Delete(workingDirectory, recursive: true); }
    }

    private static ConversationSession Seed(string workingDirectory)
    {
        var conversation = new ConversationSession(workingDirectory, "fixture", null);
        conversation.Append(new ChatMessage(ChatRole.User, "first"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "answer one"));
        conversation.Append(new ChatMessage(ChatRole.User, "second"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "answer two"));
        return conversation;
    }

    private sealed class SummaryClient(bool fail = false, Action? onRequest = null,
        string response = "Earlier context summary", Exception? error = null,
        Action<IReadOnlyList<ChatMessage>>? onMessages = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            onRequest?.Invoke();
            onMessages?.Invoke(messages.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            return error is not null
                ? Task.FromException<ChatResponse>(error)
                : fail
                    ? Task.FromException<ChatResponse>(new IOException("summary failed"))
                    : Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, response)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            onRequest?.Invoke();
            onMessages?.Invoke(messages.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            if (error is not null) throw error;
            if (fail) throw new IOException("summary failed");
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(response)])
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class OverflowThenSuccessClient : IChatClient
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requestCount);
            cancellationToken.ThrowIfCancellationRequested();
            return request == 1
                ? Task.FromException<ChatResponse>(new HttpRequestException("context_length_exceeded", null,
                    HttpStatusCode.BadRequest))
                : Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "retried answer")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var request = Interlocked.Increment(ref _requestCount);
            cancellationToken.ThrowIfCancellationRequested();
            if (request == 1)
                throw new HttpRequestException("context_length_exceeded", null, HttpStatusCode.BadRequest);
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("retried answer")])
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
