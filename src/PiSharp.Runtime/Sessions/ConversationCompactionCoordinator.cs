using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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
    Action<AgentSession, int> adoptHistory)
{
    private int _isCompacting;

    public bool IsCompacting => Volatile.Read(ref _isCompacting) != 0;

    public async Task<ConversationCompactionResult?> CompactAsync(string? focus, CancellationToken cancellationToken,
        bool adoptExecutionHistory = true)
    {
        var policy = getPolicy();
        var keepRecentTokens = policy?.KeepRecentTokens ?? getKeepRecentTokens();
        var plan = PreparePlan(conversation, keepRecentTokens);
        if (plan is null) return null;

        var tokensBefore = EstimateContextTokens();
        var details = ConversationCompactionMetadata.CollectDetails(conversation, plan);
        var summaryText = "";
        Volatile.Write(ref _isCompacting, 1);
        try
        {
            var summary = await SummarizeAsync(plan, focus, cancellationToken);
            summaryText = summary.Text + ConversationCompactionMetadata.FormatFileOperations(details);
            var previousHead = conversation.Tree.HeadId;
            try
            {
                conversation.AppendCompaction(plan, summaryText, keepRecentTokens, tokensBefore, details);
                var usageRecords = CompactionUsageAccounting.Records(summary, conversation.Model, getPricing());
                var usage = CompactionUsageAccounting.Aggregate(usageRecords);
                foreach (var record in usageRecords) conversation.AppendUsage(record);
                var messages = conversation.ContextMessages();
                var restored = adoptExecutionHistory ? await restoreHistory(messages, cancellationToken) : null;
                if (save is not null) await save(cancellationToken);
                if (restored is not null) adoptHistory(restored, messages.Count);
                return new ConversationCompactionResult(summaryText, plan.FirstKeptEntryId, tokensBefore,
                    ConversationCompactionMetadata.EstimateTokens(messages), usage, details);
            }
            catch
            {
                conversation.Tree.Select(previousHead);
                throw;
            }
        }
        finally { Volatile.Write(ref _isCompacting, 0); }
    }

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

    private static int EstimateEntryTokens(ConversationSession.CompactionContextEntry entry)
    {
        var estimated = 0L;
        foreach (var message in entry.Messages)
            estimated += ConversationCompactionMetadata.EstimateTokens([message]);
        return (int)Math.Min(int.MaxValue, estimated);
    }

    private static List<ChatMessage> SummarizableMessages(
        IReadOnlyList<ConversationSession.CompactionContextEntry> entries, int start, int end) => entries
        .Skip(start).Take(Math.Max(0, end - start))
        .SelectMany(entry => entry.Messages)
        .Where(message => message.Role != ChatRole.System)
        .ToList();

    private static int FindTurnStart(IReadOnlyList<ConversationSession.CompactionContextEntry> entries,
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
