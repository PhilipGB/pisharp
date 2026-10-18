using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Sessions;

/// <summary>Plans, summarizes, persists, and restores the model context at a canonical compaction boundary.</summary>
internal sealed class ConversationCompactionCoordinator(
    ConversationSession conversation,
    PiAgent agent,
    Func<AutoCompactionPolicy?> getPolicy,
    Func<int?> getKeepRecentTokens,
    Func<ModelPricing?> getPricing,
    Func<CancellationToken, Task>? save,
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<AgentSession>> restoreHistory,
    Action<AgentSession, int> adoptHistory,
    Func<bool> hasBeforeCompactHandlers,
    Func<NativeExtensions.PiSharpExtensionSessionBeforeCompactEvent, CancellationToken,
        Task<(NativeExtensions.PiSharpExtensionSessionBeforeCompactResult? Result, ResourceSourceInfo? SourceInfo)>> dispatchBeforeCompact)
{
    private int _isCompacting;

    public bool IsCompacting => Volatile.Read(ref _isCompacting) != 0;
    public bool LastCompactionFromExtension { get; private set; }
    public bool LastCompactionCancelledByExtension { get; private set; }

    public async Task<ConversationCompactionResult?> CompactAsync(string? focus, CancellationToken cancellationToken,
        bool adoptExecutionHistory = true,
        NativeExtensions.PiSharpExtensionCompactionReason reason = NativeExtensions.PiSharpExtensionCompactionReason.Manual,
        bool willRetry = false)
    {
        LastCompactionFromExtension = false;
        LastCompactionCancelledByExtension = false;
        var policy = getPolicy();
        var keepRecentTokens = policy?.KeepRecentTokens ?? getKeepRecentTokens();
        var plan = PreparePlan(conversation, keepRecentTokens);
        if (plan is null) return null;

        var tokensBefore = EstimateContextTokens();
        var details = ConversationCompactionMetadata.CollectDetails(conversation, plan);
        var summaryText = "";
        var previousHead = conversation.Tree.HeadId;
        Volatile.Write(ref _isCompacting, 1);
        try
        {
            NativeExtensions.PiSharpExtensionCustomCompaction? customCompaction = null;
            if (hasBeforeCompactHandlers())
            {
                var activePath = conversation.Tree.ActivePath();
                var preparation = new NativeExtensions.PiSharpExtensionCompactionPreparation(
                    plan.FirstKeptEntryId, plan.MessagesToSummarize, plan.TurnPrefixMessages ?? [], plan.IsSplitTurn,
                    tokensBefore, plan.PreviousSummary,
                    new NativeExtensions.PiSharpExtensionCompactionDetails(details.ReadFiles, details.ModifiedFiles));
                var beforeCompact = new NativeExtensions.PiSharpExtensionSessionBeforeCompactEvent(reason, willRetry,
                    preparation, activePath.Select(entry => new NativeExtensions.PiSharpExtensionSessionBranchEntry(
                        entry.Id, entry.ParentId, entry.Type, entry.Timestamp)), focus);
                var (decision, decisionSource) = await dispatchBeforeCompact(beforeCompact, cancellationToken)
                    .ConfigureAwait(false);
                if (decision?.Cancel == true)
                {
                    LastCompactionCancelledByExtension = true;
                    throw new InvalidOperationException("Compaction cancelled by extension.");
                }

                customCompaction = decision?.Compaction;
                LastCompactionFromExtension = customCompaction is not null;
                if (customCompaction is not null)
                {
                    try { ValidateCustomCompaction(customCompaction, conversation, plan.FirstKeptEntryId); }
                    catch (InvalidDataException error) when (decisionSource is not null)
                    {
                        throw new InvalidDataException(
                            $"Extension '{decisionSource.Path}' supplied an invalid custom compaction: {error.Message}",
                            error);
                    }
                }
            }

            if (conversation.Tree.HeadId != previousHead)
                throw new InvalidOperationException("Conversation changed during compaction.");

            var summary = customCompaction is null
                ? await SummarizeAsync(plan, focus, cancellationToken).ConfigureAwait(false)
                : new PiAgent.CompactionSummary(customCompaction.Summary,
                    customCompaction.Usage is null ? null : CreateUsageDetails(customCompaction.Usage));
            summaryText = customCompaction is null
                ? summary.Text + ConversationCompactionMetadata.FormatFileOperations(details)
                : customCompaction.Summary;
            var firstKeptEntryId = customCompaction?.FirstKeptEntryId ?? plan.FirstKeptEntryId;
            var compactionTokensBefore = customCompaction?.TokensBefore ?? tokensBefore;
            var resultDetails = customCompaction is null
                ? details
                : new ConversationCompactionDetails([], []);
            try
            {
                if (conversation.Tree.HeadId != previousHead)
                    throw new InvalidOperationException("Conversation changed during compaction.");
                conversation.AppendCompaction(plan, summaryText, keepRecentTokens, compactionTokensBefore,
                    resultDetails, firstKeptEntryId, customCompaction?.Details, LastCompactionFromExtension);
                var usageRecords = CompactionUsageAccounting.Records(summary, conversation.Model, getPricing());
                var usage = CompactionUsageAccounting.Aggregate(usageRecords);
                foreach (var record in usageRecords) conversation.AppendUsage(record);
                var messages = conversation.ContextMessages();
                var restored = adoptExecutionHistory ? await restoreHistory(messages, cancellationToken) : null;
                if (save is not null) await save(cancellationToken);
                if (restored is not null) adoptHistory(restored, messages.Count);
                return new ConversationCompactionResult(summaryText, firstKeptEntryId, compactionTokensBefore,
                    ConversationCompactionMetadata.EstimateTokens(messages), usage, resultDetails,
                    LastCompactionFromExtension, customCompaction?.Details);
            }
            catch
            {
                conversation.Tree.Select(previousHead);
                throw;
            }
        }
        finally { Volatile.Write(ref _isCompacting, 0); }
    }

    private static void ValidateCustomCompaction(NativeExtensions.PiSharpExtensionCustomCompaction custom,
        ConversationSession conversation, string preparedFirstKeptEntryId)
    {
        if (string.IsNullOrWhiteSpace(custom.Summary) || custom.Summary.Length > 64 * 1024)
            throw new InvalidDataException("Extension compaction summary must be nonempty and at most 64KB.");
        if (custom.TokensBefore < 0)
            throw new InvalidDataException("Extension compaction token count cannot be negative.");
        if (custom.Details is { } details &&
            System.Text.Encoding.UTF8.GetByteCount(details.GetRawText()) > 1024 * 1024)
            throw new InvalidDataException("Extension compaction details cannot exceed 1MB.");
        conversation.ValidateCompactionBoundary(custom.FirstKeptEntryId);

        var contextEntries = conversation.CompactionContextEntries(out var previousSummary);
        var start = previousSummary is null ? 0 : 1;
        var customBoundaryIndex = contextEntries.ToList().FindIndex(entry => entry.SourceEntryId == custom.FirstKeptEntryId);
        var preparedBoundaryIndex = contextEntries.ToList().FindIndex(entry => entry.SourceEntryId == preparedFirstKeptEntryId);
        if (customBoundaryIndex <= start || preparedBoundaryIndex < customBoundaryIndex)
            throw new InvalidDataException("Extension compaction boundary must be a safe retained entry no later than the prepared boundary.");
        var summarizedPrefix = contextEntries.Skip(start).Take(customBoundaryIndex - start)
            .SelectMany(entry => entry.Messages).ToArray();
        if (!HasCompleteToolCallPairs(summarizedPrefix))
            throw new InvalidDataException("Extension compaction boundary splits an incomplete tool call/result pair.");

        if (custom.Usage is { } usage &&
            (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.CachedInputTokens < 0 ||
             usage.ReasoningTokens < 0 || usage.TotalTokens < 0 || usage.CachedWriteTokens < 0))
            throw new InvalidDataException("Extension compaction usage counts cannot be negative.");
    }

    internal static bool HasCompleteToolCallPairs(IReadOnlyList<ChatMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
                if (string.IsNullOrWhiteSpace(call.CallId) || !seen.Add(call.CallId) || !pending.Add(call.CallId))
                    return false;
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
                if (!pending.Remove(result.CallId)) return false;
        }
        return pending.Count == 0;
    }

    private static UsageDetails CreateUsageDetails(NativeExtensions.PiSharpExtensionCompactionUsage usage) => new()
    {
        InputTokenCount = usage.InputTokens,
        OutputTokenCount = usage.OutputTokens,
        CachedInputTokenCount = usage.CachedInputTokens,
        ReasoningTokenCount = usage.ReasoningTokens,
        TotalTokenCount = usage.TotalTokens,
        AdditionalCounts = new() { ["cacheWriteTokens"] = usage.CachedWriteTokens }
    };

    public static ConversationSession.CompactionPlan? PreparePlan(ConversationSession conversation,
        int? keepRecentTokens)
    {
        if (keepRecentTokens is < 0) throw new ArgumentOutOfRangeException(nameof(keepRecentTokens));
        var path = conversation.Tree.ActivePath();
        if (path.LastOrDefault(node => node.Type is not ("usage" or "context_projection"))?.Type == "compaction")
            return null;

        var entries = conversation.CompactionContextEntries(out var previousSummary);
        var start = previousSummary is null ? 0 : 1;
        if (entries.Count <= start) return null;
        var cutPoints = Enumerable.Range(start, entries.Count - start)
            .Where(index => entries[index].SourceEntryId is not null &&
                entries[index].Messages.Any(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant))
            .ToArray();
        if (cutPoints.Length == 0) return null;

        int cutIndex;
        if (keepRecentTokens is null)
        {
            cutIndex = cutPoints.LastOrDefault(index =>
                entries[index].Messages.Any(message => message.Role == ChatRole.User), -1);
            if (cutIndex < 0) return null;
        }
        else
        {
            cutIndex = cutPoints[0];
            long accumulatedTokens = 0;
            for (var index = entries.Count - 1; index >= start; index--)
            {
                accumulatedTokens += EstimateEntryTokens(entries[index]);
                if (accumulatedTokens < keepRecentTokens.Value) continue;
                cutIndex = cutPoints.FirstOrDefault(candidate => candidate >= index, cutPoints[^1]);
                var recoveryBoundary = conversation.RecoveryOmittedAssistantAfter(entries[cutIndex].SourceIndex);
                if (recoveryBoundary is not null)
                {
                    var recoveryTurnStart = FindTurnStart(entries, cutIndex, start);
                    var recoverySplitTurn = recoveryTurnStart >= start;
                    var recoveryHistoryEnd = recoverySplitTurn ? recoveryTurnStart : cutIndex + 1;
                    var recoveryHistory = SummarizableMessages(entries, start, recoveryHistoryEnd);
                    var recoveryTurnPrefix = recoverySplitTurn
                        ? SummarizableMessages(entries, recoveryTurnStart, cutIndex + 1)
                        : [];
                    if (recoveryHistory.Count > 0 || recoveryTurnPrefix.Count > 0)
                        return new ConversationSession.CompactionPlan(recoveryBoundary, recoveryHistory,
                            recoveryTurnPrefix, recoverySplitTurn, previousSummary);
                }
                break;
            }
        }

        if (cutIndex <= start || entries[cutIndex].SourceEntryId is not { } firstKeptEntryId) return null;
        var startsTurn = entries[cutIndex].Messages.Any(message => message.Role == ChatRole.User);
        var turnStart = startsTurn ? -1 : FindTurnStart(entries, cutIndex, start);
        var splitTurn = !startsTurn && turnStart >= start;
        var historyEnd = splitTurn ? turnStart : cutIndex;
        var history = SummarizableMessages(entries, start, historyEnd);
        var turnPrefix = splitTurn ? SummarizableMessages(entries, turnStart, cutIndex) : [];
        if (history.Count == 0 && turnPrefix.Count == 0) return null;

        return new ConversationSession.CompactionPlan(firstKeptEntryId, history, turnPrefix, splitTurn, previousSummary);
    }

    private async Task<PiAgent.CompactionSummary> SummarizeAsync(ConversationSession.CompactionPlan plan,
        string? focus, CancellationToken cancellationToken)
    {
        if (!plan.IsSplitTurn)
            return await agent.SummarizeAsync(plan.MessagesToSummarize, focus, cancellationToken,
                previousSummary: plan.PreviousSummary);

        var historyText = plan.PreviousSummary ?? "No prior history.";
        UsageDetails? usage = null;
        var physicalUsage = new List<UsageRecord>();
        if (plan.MessagesToSummarize.Count > 0)
        {
            var history = await agent.SummarizeAsync(plan.MessagesToSummarize, focus, cancellationToken,
                previousSummary: plan.PreviousSummary);
            historyText = history.Text;
            usage = history.Usage;
            if (history.PhysicalUsage is { } historyUsage) physicalUsage.AddRange(historyUsage);
        }

        var turn = await agent.SummarizeAsync(plan.TurnPrefixMessages ?? [], null, cancellationToken, turnPrefix: true);
        usage = AddUsage(usage, turn.Usage);
        if (turn.PhysicalUsage is { } turnUsage) physicalUsage.AddRange(turnUsage);
        return new PiAgent.CompactionSummary(
            $"{historyText}\n\n---\n\n**Turn Context (split turn):**\n\n{turn.Text}", usage, physicalUsage.Count > 0 ? physicalUsage : null);
    }

    private int EstimateContextTokens()
    {
        var context = conversation.ContextMessages();
        var withSystem = new List<ChatMessage>(context.Count + 1)
        {
            new(ChatRole.System, agent.SystemInstructions)
        };
        withSystem.AddRange(context);
        var messages = ConversationCompactionMetadata.EstimateTokens(withSystem);
        var tools = JsonSerializer.Serialize(agent.ToolDeclarations, AIJsonUtilities.DefaultOptions).Length;
        return (int)Math.Min(int.MaxValue, (long)messages + (tools + 3L) / 4);
    }

    private static int EstimateEntryTokens(ConversationContextProjection.CompactionContextEntry entry)
    {
        var estimated = 0L;
        foreach (var message in entry.Messages)
            estimated += ConversationCompactionMetadata.EstimateTokens([message]);
        return (int)Math.Min(int.MaxValue, estimated);
    }

    private static List<ChatMessage> SummarizableMessages(
        IReadOnlyList<ConversationContextProjection.CompactionContextEntry> entries, int start, int end) => entries
        .Skip(start).Take(Math.Max(0, end - start))
        .SelectMany(entry => entry.Messages)
        .Where(message => message.Role != ChatRole.System)
        .ToList();

    private static int FindTurnStart(IReadOnlyList<ConversationContextProjection.CompactionContextEntry> entries,
        int from, int start)
    {
        for (var index = from; index >= start; index--)
            if (entries[index].Messages.Any(message => message.Role == ChatRole.User)) return index;
        return -1;
    }

    private static UsageDetails? AddUsage(UsageDetails? first, UsageDetails? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        var combined = new UsageDetails();
        combined.Add(first);
        combined.Add(second);
        return combined;
    }
}
