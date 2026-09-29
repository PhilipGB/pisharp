using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

internal static class ConversationCompactionMetadata
{
    public static int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        long characters = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                characters += content switch
                {
                    TextContent text => text.Text.Length,
                    TextReasoningContent reasoning => reasoning.Text.Length,
                    FunctionCallContent call => call.Name.Length + JsonSerializer.Serialize(call.Arguments,
                        AIJsonUtilities.DefaultOptions).Length,
                    FunctionResultContent result => EstimateResultCharacters(result.Result),
                    DataContent => 4_800,
                    _ => 0
                };
                if (characters >= 4L * int.MaxValue) return int.MaxValue;
            }
        }
        return (int)((characters + 3) / 4);
    }

    public static ConversationCompactionDetails CollectDetails(ConversationSession conversation,
        ConversationSession.CompactionPlan plan)
    {
        var read = new HashSet<string>(StringComparer.Ordinal);
        var modified = new HashSet<string>(StringComparer.Ordinal);
        var path = conversation.Tree.ActivePath();
        if (path.LastOrDefault(node => node.Type == "compaction") is { } previous)
        {
            var original = PiJsonlSessionInterchange.OriginalEntry(previous);
            if (original is not { } originalEntry ||
                !originalEntry.TryGetProperty("fromHook", out var fromHook) || fromHook.ValueKind != JsonValueKind.True)
            {
                ReadFiles(previous.Payload, read, modified);
                if (original is { } imported) ReadFiles(imported, read, modified);
            }
        }

        foreach (var message in plan.MessagesToSummarize.Concat(plan.TurnPrefixMessages ?? []))
        {
            if (message.Role != ChatRole.Assistant) continue;
            foreach (var call in message.Contents.OfType<FunctionCallContent>())
            {
                if (call.Arguments?.TryGetValue("path", out var pathValue) != true) continue;
                var filePath = pathValue switch
                {
                    string text => text,
                    JsonElement { ValueKind: JsonValueKind.String } value => value.GetString(),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(filePath)) continue;
                switch (call.Name)
                {
                    case "read":
                        read.Add(filePath);
                        break;
                    case "write":
                    case "edit":
                        modified.Add(filePath);
                        break;
                }
            }
        }

        var summarizedCallIds = plan.MessagesToSummarize.Concat(plan.TurnPrefixMessages ?? [])
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Select(call => call.CallId).ToHashSet(StringComparer.Ordinal);
        if (summarizedCallIds.Count > 0)
        {
            var activePath = conversation.Tree.ActivePath();
            var firstKeptIndex = activePath.ToList().FindIndex(node => node.Id == plan.FirstKeptEntryId);
            foreach (var node in activePath.Take(firstKeptIndex < 0 ? 0 : firstKeptIndex))
            {
                if (ConversationSession.NestedToolCallsFor(node) is not { } nestedCalls) continue;
                var parentCallId = ConversationSession.RestoreEntry(node).Contents
                    .OfType<FunctionResultContent>().FirstOrDefault()?.CallId;
                if (parentCallId is null || !summarizedCallIds.Contains(parentCallId)) continue;
                foreach (var call in nestedCalls.Calls)
                {
                    if (call.Arguments is not { ValueKind: JsonValueKind.Object } arguments ||
                        !arguments.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(pathValue.GetString())) continue;
                    var filePath = pathValue.GetString()!;
                    switch (call.Name)
                    {
                        case "read":
                            read.Add(filePath);
                            break;
                        case "write":
                        case "edit":
                            modified.Add(filePath);
                            break;
                    }
                }
            }
        }

        read.ExceptWith(modified);
        return new ConversationCompactionDetails(read.Order(StringComparer.Ordinal).ToArray(),
            modified.Order(StringComparer.Ordinal).ToArray());
    }

    public static string FormatFileOperations(ConversationCompactionDetails details)
    {
        var sections = new List<string>();
        if (details.ReadFiles.Count > 0)
            sections.Add("<read-files>\n" + string.Join("\n", details.ReadFiles) + "\n</read-files>");
        if (details.ModifiedFiles.Count > 0)
            sections.Add("<modified-files>\n" + string.Join("\n", details.ModifiedFiles) + "\n</modified-files>");
        return sections.Count == 0 ? "" : "\n\n" + string.Join("\n\n", sections);
    }

    private static long EstimateResultCharacters(object? value) => value switch
    {
        null => 0,
        string text => text.Length,
        _ => JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions).Length
    };

    private static void ReadFiles(JsonElement source, HashSet<string> read, HashSet<string> modified)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("details", out var details) ||
            details.ValueKind != JsonValueKind.Object) return;
        AddFiles(details, "readFiles", read);
        AddFiles(details, "modifiedFiles", modified);
    }

    private static void AddFiles(JsonElement details, string name, HashSet<string> files)
    {
        if (!details.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                files.Add(value.GetString()!);
    }
}
