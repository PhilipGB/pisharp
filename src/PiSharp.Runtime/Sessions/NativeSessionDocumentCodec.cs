using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using static PiSharp.Runtime.Sessions.ConversationMessageCodec;
using static PiSharp.Runtime.Sessions.ConversationContextProjection;

namespace PiSharp.Runtime.Sessions;

/// <summary>Validates native versioned documents across every branch before restoring the selected tree.</summary>
internal static class NativeSessionDocumentCodec
{
    public static (Document Document, ConversationTree Tree) Parse(string json)
    {
        var document = JsonSerializer.Deserialize<Document>(json) ?? throw new InvalidDataException("Empty session.");
        if (document.Version is not (1 or ConversationSession.FormatVersion) || string.IsNullOrWhiteSpace(document.Id) ||
            string.IsNullOrWhiteSpace(document.WorkingDirectory) || string.IsNullOrWhiteSpace(document.Model) || document.Entries is null)
            throw new InvalidDataException("Unsupported or incomplete session document.");
        if (document.Entries.Any(entry => entry.Type == "chat" && entry.Payload.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Session contains an invalid chat entry.");
        if (document.Entries.Any(entry => entry.Type == "bash_execution" && entry.Payload.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("Session contains an invalid Bash execution entry.");
        if (document.Version == 1 && document.Entries.Any(entry => entry.Type == "chat" &&
            (entry.Payload.Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)?.Contents.Any(content =>
                content is FunctionCallContent or FunctionResultContent) ?? false)))
            throw new InvalidDataException("v1 tool turns did not persist failure state; refusing an unsafe import.");
        var entries = document.Version == 1
            ? document.Entries.Select(entry => entry.Type == "chat"
                ? entry with { Payload = ConversationMessageCodec.EncodeLegacyMessage(entry.Payload) }
                : entry).ToArray()
            : document.Entries;
        foreach (var entry in entries) ValidateCheckpoint(entry);
        // Validate every branch, not merely the currently selected path.
        foreach (var entry in entries.Where(entry => entry.Type == "chat")) _ = Restore(entry.Payload, entry.Id);
        foreach (var entry in entries.Where(entry => entry.Type == "bash_execution"))
            _ = RestoreBashExecution(entry.Payload, entry.Id);
        foreach (var entry in entries.Where(entry => entry.Type == "usage"))
            ValidateUsage(entry.Payload.Deserialize<UsageRecord>() ??
                throw new InvalidDataException($"Invalid usage record at {entry.Id}."), entry.Id);
        var tree = ConversationTree.FromEntries(entries);
        foreach (var node in entries.Where(entry => entry.Type == "compaction"))
        {
            if (node.Payload.ValueKind != JsonValueKind.Object ||
                !node.Payload.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(summary.GetString()) || summary.GetString()!.Length > 64 * 1024 ||
                !node.Payload.TryGetProperty("firstKeptEntryId", out var kept) ||
                kept.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                node.Payload.TryGetProperty("fromHook", out var fromHook) &&
                    fromHook.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Invalid compaction at {node.Id}.");
            if (kept.ValueKind == JsonValueKind.Null || kept.GetString() == node.Id) continue;
            tree.Select(node.ParentId);
            var path = tree.ActivePath();
            var boundary = path.ToList().FindIndex(entry => entry.Id == kept.GetString());
            var previous = path.ToList().FindLastIndex(entry => entry.Type == "compaction");
            var boundaryRole = boundary >= 0 ? ContextMessageForNode(path[boundary])?.Role : null;
            if (boundary <= previous || boundary >= path.Count ||
                PiJsonlEntryCodec.OriginalEntry(node) is null &&
                boundaryRole != ChatRole.User && boundaryRole != ChatRole.Assistant)
                throw new InvalidDataException($"Invalid compaction boundary at {node.Id}.");
        }
        tree.Select(document.HeadId); // An explicit null selection is distinct from the last appended entry.
        return (document, tree);
    }

    internal static void ValidateUsage(UsageRecord usage, string id)
    {
        if (string.IsNullOrWhiteSpace(usage.Model) || string.IsNullOrWhiteSpace(usage.Source) ||
            usage.InputTokens is < 0 || usage.OutputTokens is < 0 || usage.CachedInputTokens is < 0 ||
            usage.InputIncludingCachedTokens is < 0 || usage.ReasoningTokens is < 0 ||
            usage.TotalTokens is < 0 || usage.CachedWriteTokens is < 0 || usage.Cost < 0 ||
            (usage.InputIncludingCachedTokens is long totalInput && usage.InputTokens is long input &&
                usage.CachedInputTokens is long cached &&
                (input > long.MaxValue - cached || input + cached != totalInput)))
            throw new InvalidDataException($"Invalid usage record at {id}.");
    }

    private static void ValidateCheckpoint(ConversationNode node)
    {
        if (node.Type is not ("run_started" or "run_finished" or "run_recovered" or "tool_intent" or "tool_outcome" or "tool_skipped" or "assistant_progress")) return;
        var value = node.Payload;
        static bool HasString(JsonElement element, string key) => element.TryGetProperty(key, out var field) &&
            field.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(field.GetString());
        if (value.ValueKind != JsonValueKind.Object || !HasString(value, "runId") ||
            ((node.Type is "tool_intent" or "tool_outcome" or "tool_skipped") && !HasString(value, "operationId")) ||
            (node.Type == "run_started" && !HasString(value, "prompt")) ||
            (node.Type == "tool_intent" && (!HasString(value, "name") ||
                !value.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)) ||
            (node.Type == "assistant_progress" && !HasString(value, "text")) ||
            (node.Type == "run_finished" && (!value.TryGetProperty("completed", out var completed) ||
                completed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) ||
            (node.Type == "run_recovered" && (!value.TryGetProperty("unknownOperations", out var unknown) ||
                unknown.ValueKind != JsonValueKind.Array)))
            throw new InvalidDataException($"Invalid checkpoint {node.Type} at {node.Id}.");
    }

    internal sealed record Document(int Version, string Id, string WorkingDirectory, string Model, string? Endpoint,
        string? Name, string? HeadId, ConversationNode[] Entries, string? Provider = null, JsonElement? PiHeader = null,
        string? ParentSessionPath = null);
}
