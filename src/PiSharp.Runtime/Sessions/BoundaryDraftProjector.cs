using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Sessions;

/// <summary>Applies the same validated canonical operations to previews and committed boundaries.</summary>
internal static class BoundaryDraftProjector
{
    internal static IReadOnlyList<ConversationNode> Apply(ConversationSession session,
        IReadOnlyList<PiSharpSessionBoundaryDraft> drafts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count > 128) throw new InvalidDataException("A boundary cannot contain more than 128 drafts.");
        var previousHead = session.Tree.HeadId;
        var appended = new List<ConversationNode>();
        try
        {
            foreach (var draft in drafts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previousCount = session.Tree.Entries.Count;
                try { ApplyOne(session, draft); }
                finally { appended.AddRange(session.Tree.Entries.Skip(previousCount)); }
            }
            if (appended.Sum(entry => (long)Encoding.UTF8.GetByteCount(entry.Payload.GetRawText())) > 8 * 1024 * 1024)
                throw new InvalidDataException("Boundary entries cannot exceed 8MB.");
            ValidateContext(session.ContextMessages());
            return appended.AsReadOnly();
        }
        catch
        {
            Rollback(session, appended, previousHead);
            throw;
        }
    }

    internal static void Rollback(ConversationSession session, IReadOnlyList<ConversationNode> appended,
        string? previousHead)
    {
        for (var index = appended.Count - 1; index >= 0; index--)
            session.Tree.RollbackAppend(appended[index], index == 0 ? previousHead : appended[index - 1].Id);
    }

    private static ConversationNode ApplyOne(ConversationSession session, PiSharpSessionBoundaryDraft draft)
    {
        switch (draft)
        {
            case PiSharpCustomEntryDraft custom:
                ValidateCustomType(custom.CustomType);
                var data = custom.Data ?? JsonSerializer.SerializeToElement<object?>(null);
                ValidateJson(data, "Custom data");
                using (var change = session.BeginExtensionStateAppend(custom.CustomType, data, null))
                {
                    change.Commit();
                    return session.Tree.ActivePath()[^1];
                }
            case PiSharpCustomMessageEntryDraft message:
                ValidateCustomType(message.CustomType);
                ValidateJson(message.Details, "Custom message details");
                if (message.Content.Any(part => part is not (TextContent or DataContent)))
                    throw new InvalidDataException("Custom messages support text and image content.");
                return session.Tree.Append("custom_message", JsonSerializer.SerializeToElement(new
                {
                    piOriginalEntry = new
                    {
                        type = "custom_message",
                        customType = message.CustomType,
                        content = PiJsonlEntryCodec.EncodeBoundaryContent(message.Content),
                        display = message.Display,
                        details = message.Details
                    }
                }));
            case PiSharpContextEditEntryDraft edit:
                var target = session.Tree.ActivePath().FirstOrDefault(entry => entry.Id == edit.TargetId);
                if (target is null) throw new InvalidDataException("Context edit target must exist on the active branch.");
                var targetMessage = ConversationContextProjection.ContextMessageForNode(target);
                if (target.Type is not ("chat" or "custom_message") || targetMessage is null ||
                    targetMessage.Role == ChatRole.System)
                    throw new InvalidDataException("Context edit target does not contribute editable model content.");
                if (edit.Replacement is { } content && content.Any(part =>
                        targetMessage.Role == ChatRole.Assistant
                            ? part is not (TextContent or TextReasoningContent or FunctionCallContent)
                            : part is not (TextContent or DataContent)))
                    throw new InvalidDataException("Context edit content is not valid for the target message role.");
                var replacement = edit.Replacement is null ? (JsonElement?)null :
                    JsonSerializer.SerializeToElement(new { content = PiJsonlEntryCodec.EncodeBoundaryContent(edit.Replacement) });
                return session.Tree.Append("context_edit", JsonSerializer.SerializeToElement(new { targetId = edit.TargetId, replacement }));
            case PiSharpCompactionEntryDraft compaction:
                ValidateCompaction(session, compaction);
                var entry = session.Tree.Append("compaction", JsonSerializer.SerializeToElement(new
                {
                    summary = compaction.Summary,
                    firstKeptEntryId = compaction.FirstKeptEntryId,
                    tokensBefore = AutoCompactionPolicy.Estimate(session.ContextMessages(), ""),
                    details = compaction.Details,
                    fromHook = true,
                    usage = compaction.Usage is not { } usage ? null : new
                    {
                        input = usage.InputTokens,
                        output = usage.OutputTokens,
                        cacheRead = usage.CachedInputTokens,
                        cacheWrite = usage.CachedWriteTokens,
                        totalTokens = usage.TotalTokens
                    },
                    nativeUsage = compaction.Usage is not { } nativeCounts ? null : UsageRecord.Create(
                        session.Model, "compaction", new UsageDetails
                        {
                            InputTokenCount = nativeCounts.InputTokens,
                            OutputTokenCount = nativeCounts.OutputTokens,
                            CachedInputTokenCount = nativeCounts.CachedInputTokens,
                            ReasoningTokenCount = nativeCounts.ReasoningTokens,
                            TotalTokenCount = nativeCounts.TotalTokens,
                            AdditionalCounts = new() { ["cacheWriteTokens"] = nativeCounts.CachedWriteTokens }
                        }, null)
                }));
                if (compaction.Usage is { } counts)
                    session.AppendUsage(UsageRecord.Create(session.Model, "compaction", new UsageDetails
                    {
                        InputTokenCount = counts.InputTokens,
                        OutputTokenCount = counts.OutputTokens,
                        CachedInputTokenCount = counts.CachedInputTokens,
                        ReasoningTokenCount = counts.ReasoningTokens,
                        TotalTokenCount = counts.TotalTokens,
                        AdditionalCounts = new() { ["cacheWriteTokens"] = counts.CachedWriteTokens }
                    }, null));
                return entry;
            default:
                throw new InvalidDataException("Unknown boundary draft.");
        }
    }

    private static void ValidateCompaction(ConversationSession session, PiSharpCompactionEntryDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Summary) || draft.Summary.Length > 64 * 1024)
            throw new InvalidDataException("Compaction summary must be nonempty and at most 64KB.");
        ValidateJson(draft.Details, "Compaction details");
        if (draft.FirstKeptEntryId is { } retained) session.ValidateCompactionBoundary(retained);
        else ValidateContext(session.ContextMessages());
        if (draft.Usage is { } usage && (usage.InputTokens < 0 || usage.OutputTokens < 0 ||
            usage.CachedInputTokens < 0 || usage.ReasoningTokens < 0 || usage.TotalTokens < 0 || usage.CachedWriteTokens < 0))
            throw new InvalidDataException("Compaction usage counts cannot be negative.");
    }

    internal static void ValidateContext(IReadOnlyList<ChatMessage> messages)
    {
        if (!ConversationCompactionCoordinator.HasCompleteToolCallPairs(messages))
            throw new InvalidDataException("Boundary context must preserve complete tool call/result pairs.");
        if (messages.Any(message => message.Role != ChatRole.User && message.Role != ChatRole.Assistant &&
                message.Role != ChatRole.System && message.Role != ChatRole.Tool))
            throw new InvalidDataException("Boundary context contains an unsupported message role.");
    }

    private static void ValidateCustomType(string customType)
    {
        if (string.IsNullOrWhiteSpace(customType) || customType.Length > 256)
            throw new InvalidDataException("Custom type must be nonempty and at most 256 characters.");
    }

    private static void ValidateJson(JsonElement? value, string name)
    {
        if (value is not { } json) return;
        if (json.ValueKind == JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(json.GetRawText()) > 1024 * 1024)
            throw new InvalidDataException(name + " must be bounded JSON of at most 1MB.");
    }
}
