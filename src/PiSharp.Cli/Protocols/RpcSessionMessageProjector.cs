using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal static class RpcSessionMessageProjector
{
    public static JsonArray Project(ConversationSession conversation, string? api)
    {
        var path = conversation.Tree.ActivePath();
        var compactionIndex = path.ToList().FindLastIndex(node => node.Type == "compaction");
        IReadOnlyList<ConversationNode> projectedNodes = path;
        var messages = new JsonArray();
        if (compactionIndex >= 0)
        {
            var compaction = path[compactionIndex];
            var keptId = compaction.Payload.GetProperty("firstKeptEntryId").GetString();
            var firstKeptIndex = path.ToList().FindIndex(node => node.Id == keptId);
            var isPiBoundary = PiJsonlSessionInterchange.OriginalEntry(compaction) is not null;
            if (firstKeptIndex < 0 || firstKeptIndex >= compactionIndex ||
                !isPiBoundary && ConversationSession.RestoreEntry(path[firstKeptIndex]).Role != ChatRole.User)
                throw new InvalidDataException("Invalid compaction boundary.");

            if (PiJsonlSessionInterchange.CompactionSystemMessage(compaction) is { } systemMessage)
                messages.Add(PiJsonlSessionInterchange.ProjectRuntimeMessage(conversation, systemMessage,
                    api, timestamp: compaction.Timestamp));
            messages.Add(new JsonObject
            {
                ["role"] = "compactionSummary",
                ["summary"] = compaction.Payload.GetProperty("summary").GetString(),
                ["tokensBefore"] = compaction.Payload.GetProperty("tokensBefore").GetInt32(),
                ["timestamp"] = compaction.Timestamp.ToUnixTimeMilliseconds()
            });

            projectedNodes = path.Skip(firstKeptIndex)
                .Where((node, offset) => firstKeptIndex + offset >= compactionIndex || !IsSystemMessage(node))
                .ToArray();
        }

        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in projectedNodes)
            if (PiJsonlSessionInterchange.TryGetContextEdit(node, out var targetId, out var replacement))
                edits[targetId] = replacement;

        foreach (var node in projectedNodes)
        {
            var message = ProjectNode(conversation, node, api);
            if (message is null) continue;
            if (edits.TryGetValue(node.Id, out var replacement) &&
                message["role"]?.GetValue<string>() is "user" or "assistant" or "toolResult" or "custom")
            {
                if (replacement.ValueKind == JsonValueKind.Null) continue;
                if (replacement.ValueKind == JsonValueKind.Object && replacement.TryGetProperty("content", out var content))
                    message["content"] = ProjectReplacementContent(message["role"]!.GetValue<string>(), content);
            }
            messages.Add(message);
        }
        return messages;
    }

    private static JsonObject? ProjectNode(ConversationSession conversation, ConversationNode node, string? api)
    {
        if (node.Type == "chat")
        {
            if (PiJsonlSessionInterchange.OriginalEntry(node) is not null)
                return PiJsonlSessionInterchange.ProjectEntry(conversation, node)["message"]?.DeepClone()?.AsObject();
            return PiJsonlSessionInterchange.ProjectRuntimeMessage(conversation,
                ConversationSession.RestoreEntry(node), api, timestamp: node.Timestamp);
        }

        var entry = PiJsonlSessionInterchange.ProjectEntry(conversation, node);
        if (node.Type == "bash_execution")
        {
            var bash = node.Payload.Deserialize<BashExecutionRecord>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return bash is null || bash.ExcludeFromContext
                ? null
                : entry["message"]?.DeepClone()?.AsObject();
        }
        if (node.Type == "custom_message")
        {
            if (!entry.TryGetPropertyValue("content", out var content)) return null;
            var message = new JsonObject
            {
                ["role"] = "custom",
                ["customType"] = entry["customType"]?.DeepClone(),
                ["content"] = content?.DeepClone() ?? new JsonArray(),
                ["display"] = entry["display"]?.DeepClone() ?? JsonValue.Create(false),
                ["timestamp"] = node.Timestamp.ToUnixTimeMilliseconds()
            };
            if (entry["details"] is { } details) message["details"] = details.DeepClone();
            return message;
        }
        if (node.Type == "branch_summary" && entry["summary"] is { } summary)
        {
            var message = new JsonObject
            {
                ["role"] = "branchSummary",
                ["summary"] = summary.DeepClone(),
                ["timestamp"] = node.Timestamp.ToUnixTimeMilliseconds()
            };
            if (entry["fromId"] is { } fromId) message["fromId"] = fromId.DeepClone();
            return message;
        }
        return null;
    }

    private static JsonNode? ProjectReplacementContent(string role, JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String && role is "assistant" or "toolResult")
            return new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content.GetString() });
        return JsonNode.Parse(content.GetRawText());
    }

    private static bool IsSystemMessage(ConversationNode node) =>
        node.Type == "chat" && ConversationSession.RestoreEntry(node).Role == ChatRole.System;
}
