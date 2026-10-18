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
            Assert.Equal("Compaction failed: summary failed", failed.ErrorMessage);
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
            PiSharpExtensionSessionCompactEvent? compacted = null;
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
            PiSharpExtensionSessionCompactEvent? compacted = null;
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
            Assert.Equal(PiSharpExtensionCompactionReason.Overflow, compacted.Reason);
            Assert.False(compacted.FromExtension);
            Assert.True(compacted.WillRetry);
            Assert.Equal("retry answer", conversation.ActiveMessages().Last().Text);
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
        string response = "Earlier context summary", Exception? error = null) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            onRequest?.Invoke();
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
}
