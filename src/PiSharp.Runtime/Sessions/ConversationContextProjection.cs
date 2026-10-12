using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using static PiSharp.Runtime.Sessions.ConversationMessageCodec;

namespace PiSharp.Runtime.Sessions;

/// <summary>Projects canonical branches into model context, usage estimates and compaction boundaries.</summary>
internal static class ConversationContextProjection
{
    public static ContextUsageProjection ProjectContextUsageForEstimate(IReadOnlyList<ConversationNode> path)
    {
        var compactIndex = path.ToList().FindLastIndex(node => node.Type == "compaction");
        var hasCompaction = compactIndex >= 0;
        if (path.Skip(compactIndex + 1).Any(node => node.Type == "context_projection"))
            return new(hasCompaction, false, false, 0, []);

        var usageByAssistantIndex = new Dictionary<int, long>();
        for (var index = compactIndex + 1; index < path.Count; index++)
        {
            var node = path[index];
            if (node.Type == "chat")
            {
                var message = Restore(node.Payload, node.Id);
                if (IsUsableAssistantMessage(path, index, node, message) &&
                    TryGetPiUsageTokens(node, out var piUsageTokens))
                    usageByAssistantIndex[index] = piUsageTokens;
            }

            if (node.Type != "usage") continue;
            var usage = node.Payload.Deserialize<UsageRecord>() ??
                throw new InvalidDataException($"Invalid usage record at {node.Id}.");
            if (usage.Source != "model" || ContextTokens(usage) is not > 0) continue;
            var assistantIndex = FollowingAssistantIndex(path, index);
            if (assistantIndex < 0) assistantIndex = PreviousAssistantIndex(path, index);
            if (assistantIndex < 0 || !IsUsableAssistantMessage(path, assistantIndex, path[assistantIndex],
                    ContextMessageForNode(path[assistantIndex])!)) continue;
            usageByAssistantIndex[assistantIndex] = ContextTokens(usage);
        }

        if (usageByAssistantIndex.Count == 0)
            return new(hasCompaction, false, false, 0, []);

        var latest = usageByAssistantIndex.OrderBy(item => item.Key).Last();
        var invalidatingIndex = path.ToList().FindLastIndex(node => node.Type is "context_edit" or "compaction");
        var invalidated = invalidatingIndex > latest.Key;
        var trailingMessages = invalidated ? [] : ProjectContextEntries(path.Skip(latest.Key + 1).ToArray())
            .Select(entry => entry.Message).ToArray();
        return new(hasCompaction, true, invalidated, latest.Value, trailingMessages);
    }

    private static int FollowingAssistantIndex(IReadOnlyList<ConversationNode> path, int sourceIndex)
    {
        for (var index = sourceIndex + 1; index < path.Count; index++)
        {
            if (path[index].Type is "interrupted" or "compaction") return -1;
            var message = ContextMessageForNode(path[index]);
            if (message?.Role == ChatRole.User) return -1;
            if (message?.Role == ChatRole.Assistant) return path[index].Type == "chat" ? index : -1;
        }
        return -1;
    }

    private static int PreviousAssistantIndex(IReadOnlyList<ConversationNode> path, int sourceIndex)
    {
        for (var index = sourceIndex - 1; index >= 0; index--)
        {
            var node = path[index];
            if (node.Type is "interrupted" or "compaction") return -1;
            var message = ContextMessageForNode(node);
            if (message?.Role == ChatRole.User) return -1;
            if (message?.Role == ChatRole.Assistant) return node.Type == "chat" ? index : -1;
        }
        return -1;
    }

    private static bool IsUsableAssistantMessage(IReadOnlyList<ConversationNode> path, int index,
        ConversationNode node, ChatMessage message)
    {
        if (node.Type != "chat" || message.Role != ChatRole.Assistant ||
            ChatMessageProperties.String(message.AdditionalProperties, "pisharp.stopReason") is "aborted" or "error")
            return false;
        for (var next = index + 1; next < path.Count; next++)
        {
            if (path[next].Type == "interrupted") return false;
            if (ContextMessageForNode(path[next]) is not null) break;
        }
        return true;
    }

    private static long ContextTokens(UsageRecord usage)
    {
        if (usage.TotalTokens > 0) return usage.TotalTokens;
        return SaturatingAdd(SaturatingAdd(usage.InputTokens, usage.OutputTokens),
            SaturatingAdd(usage.CachedInputTokens, usage.CachedWriteTokens));
    }

    private static bool TryGetPiUsageTokens(ConversationNode node, out long tokens)
    {
        tokens = 0;
        if (PiJsonlEntryCodec.OriginalEntry(node) is not { } entry ||
            !entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return false;

        var total = Number(usage, "totalTokens");
        tokens = total > 0 ? total : SaturatingAdd(SaturatingAdd(Number(usage, "input"), Number(usage, "output")),
            SaturatingAdd(Number(usage, "cacheRead"), Number(usage, "cacheWrite")));
        return tokens > 0;
    }

    private static long Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) && result > 0 ? result : 0;

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    /// <summary>Model input for the selected path; raw chat entries remain available to history and export.</summary>
    public static List<ChatMessage> ContextMessages(IReadOnlyList<ConversationNode> path)
    {
        var compactAt = path.ToList().FindLastIndex(node => node.Type == "compaction");
        var from = 0;
        var projectedNodes = path;
        var context = new List<ChatMessage>();
        if (compactAt >= 0)
        {
            var compact = path[compactAt].Payload;
            var kept = compact.GetProperty("firstKeptEntryId").GetString();
            from = path.ToList().FindIndex(node => node.Id == kept);
            var piBoundary = PiJsonlEntryCodec.OriginalEntry(path[compactAt]) is not null;
            if (from < 0 || from >= compactAt)
                throw new InvalidDataException("Invalid compaction boundary.");
            if (!piBoundary)
            {
                var role = ContextMessageForNode(path[from])?.Role;
                if (role != ChatRole.User && role != ChatRole.Assistant)
                    throw new InvalidDataException("Invalid compaction boundary.");
            }
            if (PiJsonlEntryCodec.CompactionSystemMessage(path[compactAt]) is { } systemMessage)
                context.Add(systemMessage);
            context.Add(CompactionSummaryMessage(compact.GetProperty("summary").GetString() ?? ""));
            projectedNodes = path.Skip(from).Where((node, offset) => from + offset >= compactAt ||
                ContextMessageForNode(node)?.Role != ChatRole.System).ToList();
        }
        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in projectedNodes)
            if (PiJsonlEntryCodec.TryGetContextEdit(node, out var targetId, out var replacement))
                edits[targetId] = replacement;
        foreach (var entry in ProjectContextEntries(projectedNodes))
        {
            var node = entry.Node;
            var message = entry.Message;
            if (edits.TryGetValue(node.Id, out var replacement))
                message = PiJsonlEntryCodec.ApplyContextEdit(node, message, replacement);
            if (message is not null) context.Add(message);
        }
        return context;
    }

    internal static ChatMessage CompactionSummaryMessage(string summary) => new(ChatRole.User,
        "The conversation history before this point was compacted into the following summary:\n\n<summary>\n" +
        summary + "\n</summary>");

    public static IReadOnlyList<CompactionContextEntry> CompactionContextEntries(IReadOnlyList<ConversationNode> path, out string? previousSummary)
    {
        var compactionIndex = path.ToList().FindLastIndex(node => node.Type == "compaction");
        previousSummary = compactionIndex < 0
            ? null
            : path[compactionIndex].Payload.GetProperty("summary").GetString() ?? "";
        var retainedFrom = 0;
        if (compactionIndex >= 0)
        {
            var firstKeptId = path[compactionIndex].Payload.GetProperty("firstKeptEntryId").GetString();
            retainedFrom = path.ToList().FindIndex(node => node.Id == firstKeptId);
            if (retainedFrom < 0 || retainedFrom >= compactionIndex)
                throw new InvalidDataException("Invalid compaction boundary.");
        }

        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var index = retainedFrom; index < path.Count; index++)
        {
            if (index == compactionIndex) continue;
            if (PiJsonlEntryCodec.TryGetContextEdit(path[index], out var targetId, out var replacement))
                edits[targetId] = replacement;
        }

        var projected = new List<CompactionContextEntry>();
        string? lastAssistantText = null;
        if (compactionIndex >= 0)
        {
            var summary = previousSummary ?? "";
            projected.Add(new CompactionContextEntry(path[compactionIndex].Id, compactionIndex,
                [new ChatMessage(ChatRole.User, summary)], IsPreviousSummary: true));
        }

        for (var index = retainedFrom; index < path.Count; index++)
        {
            if (index == compactionIndex) continue;
            var node = path[index];
            var message = ContextMessageForNode(node);
            if (message is null) continue;
            if (compactionIndex >= 0 && index < compactionIndex && message.Role == ChatRole.System) continue;
            if (edits.TryGetValue(node.Id, out var replacement))
                message = PiJsonlEntryCodec.ApplyContextEdit(node, message, replacement);
            if (message is null) continue;
            if (node.Type == "interrupted" && message.Role == ChatRole.Assistant && message.Text.Length > 0 &&
                lastAssistantText == message.Text)
                continue;
            if (message.Role == ChatRole.Assistant) lastAssistantText = message.Text;
            projected.Add(new CompactionContextEntry(node.Id, index, [message]));
        }
        return projected;
    }

    /// <summary>
    /// Return the first omitted assistant attempt when every context-visible entry after a
    /// candidate boundary is closed by recovery omission edits. This keeps compaction from
    /// stopping immediately before an invisible failed-attempt suffix.
    /// </summary>
    public static string? RecoveryOmittedAssistantAfter(IReadOnlyList<ConversationNode> path, int sourceIndex)
    {
        if (sourceIndex < 0 || sourceIndex >= path.Count - 1) return null;

        var compactIndex = path.ToList().FindLastIndex(node => node.Type == "compaction");
        var retainedFrom = 0;
        if (compactIndex >= 0)
        {
            var firstKeptId = path[compactIndex].Payload.GetProperty("firstKeptEntryId").GetString();
            retainedFrom = path.ToList().FindIndex(node => node.Id == firstKeptId);
            if (retainedFrom < 0 || retainedFrom >= compactIndex || sourceIndex < retainedFrom) return null;
        }

        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var index = retainedFrom; index < path.Count; index++)
            if (index != compactIndex && PiJsonlEntryCodec.TryGetContextEdit(path[index], out var targetId, out var replacement))
                edits[targetId] = replacement;

        var suffix = path.Skip(sourceIndex + 1).ToArray();
        ChatMessage? Project(ConversationNode node)
        {
            var message = ContextMessageForNode(node);
            return message is not null && edits.TryGetValue(node.Id, out var replacement)
                ? PiJsonlEntryCodec.ApplyContextEdit(node, message, replacement)
                : message;
        }

        bool IsIntrinsicallyVisible(ConversationNode node) =>
            node.Type != "context_edit" && ContextMessageForNode(node) is not null;

        bool IsOmitted(ConversationNode node) =>
            IsIntrinsicallyVisible(node) && Project(node) is null;

        var omittedIds = suffix.Where(IsOmitted).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var hasExternalReplacement = suffix.Any(node =>
            PiJsonlEntryCodec.TryGetContextEdit(node, out var targetId, out var replacement) &&
            replacement.ValueKind != JsonValueKind.Null && !omittedIds.Contains(targetId));
        var hasOmittedAssistant = suffix.Any(node => omittedIds.Contains(node.Id) &&
            ContextMessageForNode(node)?.Role == ChatRole.Assistant);
        if (!hasOmittedAssistant || hasExternalReplacement || suffix.Any(node =>
                node.Type == "compaction" || IsIntrinsicallyVisible(node) && !omittedIds.Contains(node.Id)))
            return null;

        return suffix.First(node => omittedIds.Contains(node.Id) &&
            ContextMessageForNode(node)?.Role == ChatRole.Assistant).Id;
    }

    internal static ChatMessage BashExecutionContextMessage(BashExecutionRecord execution)
    {
        var text = $"Ran `{execution.Command}`\n" +
            (execution.Output.Length == 0 ? "(no output)" : $"```\n{execution.Output}\n```");
        if (execution.Cancelled) text += "\n\n(command cancelled)";
        else if (execution.ExitCode is { } exitCode && exitCode != 0) text += $"\n\nCommand exited with code {exitCode}";
        if (execution.Truncated && execution.FullOutputPath is { } fullOutputPath)
            text += $"\n\n[Output truncated. Full output: {fullOutputPath}]";
        return new ChatMessage(ChatRole.User, text);
    }

    internal static ChatMessage? ContextMessageForNode(ConversationNode node)
    {
        if (node.Type == "chat") return Restore(node.Payload, node.Id);
        if (node.Type == "interrupted" &&
            PiJsonlEntryCodec.StringProperty(node.Payload, "partialAssistantText") is { Length: > 0 } partialText)
            return new ChatMessage(ChatRole.Assistant, partialText);
        if (node.Type is "custom_message" or "branch_summary")
            return PiJsonlEntryCodec.ImportedContextMessage(node);
        if (node.Type != "bash_execution") return null;
        var execution = RestoreBashExecution(node.Payload, node.Id);
        return execution.ExcludeFromContext ? null : BashExecutionContextMessage(execution);
    }

    private static List<ProjectedContextEntry> ProjectContextEntries(IReadOnlyList<ConversationNode> nodes)
    {
        var entries = new List<ProjectedContextEntry>();
        foreach (var (node, index) in nodes.Select((node, index) => (node, index)))
        {
            var message = ContextMessageForNode(node);
            if (message is null) continue;
            if (node.Type == "interrupted" && message.Text.Length > 0 &&
                entries.LastOrDefault(entry => entry.Message.Role == ChatRole.Assistant)?.Message.Text == message.Text)
                continue;
            entries.Add(new ProjectedContextEntry(node, index, message));
        }
        return entries;
    }

    internal sealed record ContextUsageProjection(bool HasCompaction, bool HasValidAssistantUsage,
        bool IsInvalidated, long UsageTokens, IReadOnlyList<ChatMessage> TrailingMessages);

    internal sealed record CompactionContextEntry(string? SourceEntryId, int SourceIndex,
        IReadOnlyList<ChatMessage> Messages, bool IsPreviousSummary = false);

    private sealed record ProjectedContextEntry(ConversationNode Node, int Index, ChatMessage Message);
}
