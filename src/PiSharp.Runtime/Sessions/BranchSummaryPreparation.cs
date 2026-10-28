using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Sessions;

/// <summary>Builds bounded branch-summary input from canonical entries without changing session state.</summary>
internal static class BranchSummaryPreparation
{
    public static BranchSummaryPreparationResult Prepare(IReadOnlyList<ConversationNode> entries, int tokenBudget = 0)
    {
        var readFiles = new HashSet<string>(StringComparer.Ordinal);
        var writtenFiles = new HashSet<string>(StringComparer.Ordinal);
        var editedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Type == "branch_summary" &&
                (!entry.Payload.TryGetProperty("fromHook", out var fromHook) || fromHook.ValueKind != System.Text.Json.JsonValueKind.True) &&
                entry.Payload.TryGetProperty("details", out var details) && details.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                AddPaths(details, "readFiles", readFiles);
                AddPaths(details, "modifiedFiles", editedFiles);
            }
        }

        var selected = new Stack<ChatMessage>();
        long totalTokens = 0;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            var message = ToMessage(entry);
            if (message is null) continue;
            if (entry.Type == "chat")
            {
                if (message.Role == ChatRole.Assistant)
                    foreach (var call in message.Contents.OfType<FunctionCallContent>())
                        AddFileOperation(call.Name, call.Arguments, readFiles, writtenFiles, editedFiles);
                if (message.Role == ChatRole.Tool && ConversationMessageCodec.NestedToolCallsFor(entry) is { } nestedCalls)
                    foreach (var call in nestedCalls.Calls)
                        AddFileOperation(call.Name, call.Arguments, readFiles, writtenFiles, editedFiles);
            }
            if (message.Role == ChatRole.Tool) continue;

            var messageTokens = EstimateEntryTokens(entry, message);
            if (tokenBudget > 0 && totalTokens + messageTokens > tokenBudget)
            {
                if ((entry.Type is "compaction" or "branch_summary") && totalTokens < tokenBudget * 0.9)
                {
                    selected.Push(message);
                    totalTokens += messageTokens;
                }
                break;
            }

            selected.Push(message);
            totalTokens += messageTokens;
        }
        var modified = new HashSet<string>(writtenFiles, StringComparer.Ordinal);
        modified.UnionWith(editedFiles);
        var orderedModified = modified.Order(StringComparer.Ordinal).ToArray();
        var orderedRead = readFiles.Where(path => !modified.Contains(path)).Order(StringComparer.Ordinal).ToArray();
        return new(selected.ToArray(), orderedRead, orderedModified, FormatFileOperations(orderedRead, orderedModified));
    }

    private static int EstimateEntryTokens(ConversationNode entry, ChatMessage message)
    {
        if (entry.Type is "compaction" or "branch_summary")
        {
            var source = PiJsonlEntryCodec.OriginalEntry(entry) ?? entry.Payload;
            var summary = PiJsonlEntryCodec.StringProperty(source, "summary") ?? "";
            return (summary.Length + 3) / 4;
        }
        if (entry.Type == "bash_execution")
        {
            var execution = ConversationMessageCodec.RestoreBashExecution(entry.Payload, entry.Id);
            return (int)Math.Min(int.MaxValue, ((long)execution.Command.Length + execution.Output.Length + 3) / 4);
        }
        return PiContextUsageEstimator.EstimateAfterUsage(0, [message]);
    }

    private static ChatMessage? ToMessage(ConversationNode entry)
    {
        if (entry.Type == "compaction" && entry.Payload.TryGetProperty("summary", out var summary) &&
            summary.ValueKind == System.Text.Json.JsonValueKind.String)
            return ConversationContextProjection.CompactionSummaryMessage(summary.GetString() ?? "");
        return ConversationContextProjection.ContextMessageForNode(entry);
    }

    private static void AddPaths(System.Text.Json.JsonElement details, string property, HashSet<string> paths)
    {
        if (!details.TryGetProperty(property, out var values) || values.ValueKind != System.Text.Json.JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray())
            if (value.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()))
                paths.Add(value.GetString()!);
    }

    private static void AddFileOperation(string name, IDictionary<string, object?>? arguments,
        HashSet<string> read, HashSet<string> written, HashSet<string> edited)
    {
        if (arguments is null || !arguments.TryGetValue("path", out var value)) return;
        var path = value switch
        {
            string text => text,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
            _ => null
        };
        if (string.IsNullOrEmpty(path)) return;
        switch (name)
        {
            case "read": read.Add(path); break;
            case "write": written.Add(path); break;
            case "edit": edited.Add(path); break;
        }
    }

    private static void AddFileOperation(string name, System.Text.Json.JsonElement? arguments,
        HashSet<string> read, HashSet<string> written, HashSet<string> edited)
    {
        if (arguments is not { ValueKind: System.Text.Json.JsonValueKind.Object } value ||
            !value.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != System.Text.Json.JsonValueKind.String ||
            string.IsNullOrEmpty(pathValue.GetString())) return;
        switch (name)
        {
            case "read": read.Add(pathValue.GetString()!); break;
            case "write": written.Add(pathValue.GetString()!); break;
            case "edit": edited.Add(pathValue.GetString()!); break;
        }
    }

    private static string FormatFileOperations(IReadOnlyList<string> readFiles, IReadOnlyList<string> modifiedFiles)
    {
        var sections = new List<string>();
        if (readFiles.Count > 0) sections.Add($"<read-files>\n{string.Join("\n", readFiles)}\n</read-files>");
        if (modifiedFiles.Count > 0) sections.Add($"<modified-files>\n{string.Join("\n", modifiedFiles)}\n</modified-files>");
        return sections.Count == 0 ? "" : $"\n\n{string.Join("\n\n", sections)}";
    }
}

internal sealed record BranchSummaryPreparationResult(IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string> ReadFiles, IReadOnlyList<string> ModifiedFiles, string FileOperationsSuffix);
