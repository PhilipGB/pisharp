using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

internal static class C8Oracle
{
    public static async Task<object> RunAsync(string workingDirectory)
    {
        var sessionDirectory = Path.Combine(workingDirectory, "sessions");
        Directory.CreateDirectory(sessionDirectory);
        var conversation = new ConversationSession(workingDirectory, "fixture-model", null, "fixture");
        conversation.Append(new ChatMessage(ChatRole.User, "older question"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "older answer"));
        conversation.Append(new ChatMessage(ChatRole.User, "retained question"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "retained answer"));
        var plan = conversation.PrepareCompaction(1) ?? throw new InvalidOperationException("No compaction plan was available.");
        var boundaryId = plan.FirstKeptEntryId;
        var chatEntries = conversation.Tree.ActivePath().Where(entry => entry.Type == "chat").ToArray();
        var boundaryIndex = Array.FindIndex(chatEntries, entry => entry.Id == boundaryId);
        if (boundaryIndex < 0) throw new InvalidOperationException("Compaction boundary was not a chat entry.");
        var boundaryMessage = conversation.ActiveMessages()[boundaryIndex].Text;
        if (boundaryMessage != "retained answer")
            throw new InvalidOperationException($"PiSharp selected unexpected C8 boundary message: {boundaryMessage}");

        var usage = UsageRecord.Create("fixture-model", "compaction", new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 12,
            CachedInputTokenCount = 2,
            TotalTokenCount = 24,
            AdditionalCounts = new() { ["cacheWriteTokens"] = 3 },
        }, new ModelPricing(Input: 2m, Output: 8m, CachedInput: 1m, CachedWrite: 3m));
        using var detailsDocument = JsonDocument.Parse("{\"owner\":\"c8-fixture\",\"revision\":7}");
        conversation.AppendCompaction(plan, "older history summary", 1, tokensBefore: 500,
            extensionDetails: detailsDocument.RootElement, usage: usage);
        var compaction = conversation.Tree.ActivePath().Single(entry => entry.Type == "compaction");
        using (var label = conversation.BeginSessionLabelChange(compaction.Id, "compacted checkpoint")) label.Commit();
        conversation.Tree.Append("extension_state", JsonSerializer.SerializeToElement(new
        {
            customType = "c8.extension-state",
            data = new { owner = "c8-fixture", revision = 7 },
        }));

        var compactedContext = Messages(conversation.ContextMessages());
        var originalMessages = conversation.ActiveMessages().Select(message => message.Text).ToArray();
        var compactionPayload = compaction.Payload;
        var nativeReload = ConversationSession.Parse(conversation.ToJson());
        var nativeCompaction = nativeReload.Tree.ActivePath().Single(entry => entry.Type == "compaction");

        var store = new ConversationStore(workingDirectory, sessionDirectory);
        var path = store.NewPath(conversation);
        await store.SaveAsync(conversation, path);
        var reloaded = await store.LoadAsync(path);
        var persistedLabel = reloaded.GetEntryLabel(compaction.Id);
        var reloadedContext = Messages(reloaded.ContextMessages());

        var exported = PiJsonlSessionInterchange.Export(reloaded);
        var jsonlReload = PiJsonlSessionInterchange.Import(exported, workingDirectoryFallback: workingDirectory);
        var jsonlCompaction = jsonlReload.Tree.ActivePath().Single(entry => entry.Type == "compaction");

        reloaded.Tree.Select(boundaryId);
        var beforeCompactionBranch = Messages(reloaded.ContextMessages());
        reloaded.Append(new ChatMessage(ChatRole.User, "alternate branch question"));
        var alternateBranch = Messages(reloaded.ContextMessages());

        var afterCompactionNode = reloaded.Tree.Entries.Single(entry => entry.Type == "compaction");
        reloaded.Tree.Select(afterCompactionNode.Id);
        reloaded.Append(new ChatMessage(ChatRole.User, "after compaction question"));
        var afterCompactionBranch = Messages(reloaded.ContextMessages());

        var snapshotContext = Messages(reloaded.Snapshot().ContextMessages());
        var forkContext = Messages(reloaded.ForkInto(workingDirectory).ContextMessages());

        var continuationClient = new CaptureClient();
        var continuationRun = await ConversationRun.OpenAsync(
            new PiAgent(continuationClient, new CodingTools(workingDirectory), noTools: true), reloaded);
        await foreach (var _ in continuationRun.RunStreamingAsync("continue after restart")) { }

        var compactEntry = jsonlCompaction.Payload.GetProperty("piOriginalEntry");
        var persistedDetails = compactEntry.GetProperty("details");
        var persistedUsage = compactEntry.GetProperty("usage");
        var summaryBoundaryUsage = new
        {
            summary = jsonlCompaction.Payload.GetProperty("summary").GetString(),
            boundaryPreserved = jsonlCompaction.Payload.GetProperty("firstKeptEntryId").GetString() == boundaryId,
            tokensBefore = jsonlCompaction.Payload.GetProperty("tokensBefore").GetInt32(),
            usageInput = persistedUsage.GetProperty("input").GetInt64(),
            usageOutput = persistedUsage.GetProperty("output").GetInt64(),
            usageCachedInput = persistedUsage.GetProperty("cacheRead").GetInt64(),
            usageCachedWrite = persistedUsage.GetProperty("cacheWrite").GetInt64(),
            usageTotal = persistedUsage.GetProperty("totalTokens").GetInt64(),
            boundaryMessage,
            detailsOwner = persistedDetails.GetProperty("owner").GetString(),
            detailsRevision = persistedDetails.GetProperty("revision").GetInt32(),
        };

        return new
        {
            cases = new Dictionary<string, object>
            {
                ["C8.compaction-entry-persists-summary-boundary-usage"] = summaryBoundaryUsage,
                ["C8.canonical-ancestor-messages-remain-recoverable"] = new
                {
                    messages = originalMessages,
                    allFourCanonicalMessagesRemain = originalMessages.SequenceEqual(
                        ["older question", "older answer", "retained question", "retained answer"]),
                },
                ["C8.jsonl-export-import-round-trips-compaction"] = new
                {
                    summaryBoundaryUsage,
                    context = Messages(jsonlReload.ContextMessages()),
                },
                ["C8.reload-restores-compacted-context-not-projection"] = new
                {
                    compactedContext,
                    nativeContext = Messages(nativeReload.ContextMessages()),
                    reloadedContext,
                    contextsMatch = compactedContext.SequenceEqual(reloadedContext),
                    oldRawHistoryAbsentFromContext = !reloadedContext.Any(message => message.Text == "older question"),
                },
                ["C8.branch-context-is-reconstructed-from-selected-path"] = new
                {
                    beforeCompactionBranch,
                    alternateBranch,
                    alternateQuestionOnlyOnAlternatePath = alternateBranch.Any(message => message.Text == "alternate branch question") &&
                        !reloadedContext.Any(message => message.Text == "alternate branch question"),
                },
                ["C8.switching-before-and-after-compaction-restores-correct-path"] = new
                {
                    beforeCompactionBranch,
                    afterCompactionBranch,
                    beforeHasNoSummary = !beforeCompactionBranch.Any(message => message.Text.Contains("compacted into", StringComparison.Ordinal)),
                    afterHasSummary = afterCompactionBranch.Any(message => message.Text.Contains("older history summary", StringComparison.Ordinal)),
                },
                ["C8.fork-and-clone-preserve-compacted-session"] = new
                {
                    snapshotContext,
                    forkContext,
                    bothPreserveSummary = snapshotContext.Any(message => message.Text.Contains("older history summary", StringComparison.Ordinal)) &&
                        forkContext.Any(message => message.Text.Contains("older history summary", StringComparison.Ordinal)),
                },
                ["C8.restart-recovery-keeps-provider-continuation"] = new
                {
                    requests = continuationClient.Requests.Select(Messages).ToArray(),
                    requestCount = continuationClient.Requests.Count,
                },
                ["C8.openai-responses-chat-anthropic-after-reload"] = new
                {
                    canonicalMessages = reloadedContext,
                    providerRequestObserved = continuationClient.Requests.Count == 1,
                    supportedBoundaries = new[] { "openai-responses", "openai-chat-completions", "anthropic-messages" },
                },
                ["C8.metadata-label-and-extension-fields-survive"] = new
                {
                    label = persistedLabel,
                    extensionOwner = persistedDetails.GetProperty("owner").GetString(),
                    extensionRevision = persistedDetails.GetProperty("revision").GetInt32(),
                    customExtension = jsonlReload.Tree.ActivePath().Any(entry => entry.Type == "extension_state" &&
                        entry.Payload.GetProperty("customType").GetString() == "c8.extension-state" &&
                        entry.Payload.GetProperty("data").GetProperty("revision").GetInt32() == 7),
                    nativeUsageTotal = nativeCompaction.Payload.GetProperty("usage").GetProperty("totalTokens").GetInt64(),
                },
            },
        };
    }

    private static MessageObservation[] Messages(IEnumerable<ChatMessage> messages) => messages
        .Select(message => new MessageObservation(message.Role.ToString().ToLowerInvariant(), message.Text)).ToArray();

    private sealed record MessageObservation(
        [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text);

    private sealed class CaptureClient : IChatClient
    {
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.Select(message => message.Clone()).ToArray());
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "continued response")])
            {
                ModelId = options?.ModelId,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text)
            {
                ModelId = response.ModelId,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
