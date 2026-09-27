using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal static class RpcSessionStatsProjector
{
    public static JsonObject Project(ConversationSession session, string? sessionFile,
        JsonElement? model, JsonObject? systemMessage)
    {
        session = session.Snapshot();
        var messages = session.Tree.Entries.Where(entry => entry.Type == "chat")
            .Select(ProjectMessageStats).ToArray();
        var usage = session.Tree.Entries.SelectMany(UsageFor)
            .ToArray();
        var input = usage.Sum(item => item.Input);
        var output = usage.Sum(item => item.Output);
        var cacheRead = usage.Sum(item => item.CacheRead);
        var cacheWrite = usage.Sum(item => item.CacheWrite);
        var tokens = input + output + cacheRead + cacheWrite;

        var result = new JsonObject();
        if (sessionFile is not null) result["sessionFile"] = sessionFile;
        result["sessionId"] = session.Id;
        result["userMessages"] = messages.Sum(message => message.UserMessages);
        result["assistantMessages"] = messages.Sum(message => message.AssistantMessages);
        result["toolCalls"] = messages.Sum(message => message.ToolCalls);
        result["toolResults"] = messages.Sum(message => message.ToolResults);
        result["totalMessages"] = messages.Length + (systemMessage is null ? 0 : 1);
        result["tokens"] = new JsonObject
        {
            ["input"] = input,
            ["output"] = output,
            ["cacheRead"] = cacheRead,
            ["cacheWrite"] = cacheWrite,
            ["total"] = tokens
        };
        result["cost"] = usage.Sum(item => item.Cost);

        if (TryGetContextWindow(model, out var contextWindow))
        {
            var latestUsage = session.LatestContextUsageTokens();
            var afterCompaction = session.Tree.ActivePath().Any(entry => entry.Type == "compaction");
            long? contextTokens = afterCompaction && latestUsage is null
                ? null
                : latestUsage ?? SessionStatistics.Calculate(session).EstimatedContextTokens;
            result["contextUsage"] = new JsonObject
            {
                ["tokens"] = contextTokens is long value ? JsonValue.Create(value) : null,
                ["contextWindow"] = contextWindow,
                ["percent"] = contextTokens is long count ? count * 100d / contextWindow : null
            };
        }
        return result;
    }

    private static bool TryGetContextWindow(JsonElement? model, out int contextWindow)
    {
        contextWindow = 0;
        return model is JsonElement value && value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("contextWindow", out var window) && window.TryGetInt32(out contextWindow) &&
            contextWindow > 0;
    }

    private static IEnumerable<SessionUsage> UsageFor(ConversationNode node)
    {
        if (node.Type == "usage")
        {
            var usage = node.Payload.Deserialize<UsageRecord>() ??
                throw new InvalidDataException($"Invalid usage record at {node.Id}.");
            yield return new SessionUsage(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens,
                usage.CachedWriteTokens, usage.Cost ?? 0m);
            yield break;
        }

        JsonElement original;
        if (node.Type == "chat")
        {
            if (ConversationSession.PiEntryFromChatPayload(node.Payload) is not { } piEntry ||
                !piEntry.TryGetProperty("message", out var message) ||
                (StringProperty(message, "role") is not ("assistant" or "toolResult"))) yield break;
            original = message;
        }
        else if (node.Type == "pi_entry" && StringProperty(node.Payload, "piType") == "usage")
        {
            if (!TryGetPiOriginalEntry(node.Payload, out original)) yield break;
        }
        else if (node.Type is "compaction" or "branch_summary")
        {
            if (!TryGetPiOriginalEntry(node.Payload, out original)) yield break;
        }
        else yield break;

        if (TryGetPiUsage(original, out var importedUsage)) yield return importedUsage;
    }

    private static MessageStats ProjectMessageStats(ConversationNode node)
    {
        var message = ConversationSession.RestoreEntry(node);
        if (ConversationSession.PiEntryFromChatPayload(node.Payload) is { } piEntry &&
            piEntry.TryGetProperty("message", out var original))
        {
            var role = StringProperty(original, "role");
            var toolCalls = 0;
            if (role == "assistant" && original.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.Array)
                toolCalls = content.EnumerateArray().Count(item => StringProperty(item, "type") == "toolCall");
            return new MessageStats(role == "user" ? 1 : 0, role == "assistant" ? 1 : 0, toolCalls,
                role == "toolResult" ? 1 : 0);
        }

        return new MessageStats(message.Role == ChatRole.User ? 1 : 0,
            message.Role == ChatRole.Assistant ? 1 : 0,
            message.Contents.OfType<FunctionCallContent>().Count(),
            message.Contents.OfType<FunctionResultContent>().Count());
    }

    private static bool TryGetPiOriginalEntry(JsonElement entry, out JsonElement original)
    {
        original = default;
        return entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("piOriginalEntry", out original) &&
            original.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetPiUsage(JsonElement entry, out SessionUsage usage)
    {
        usage = default;
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("usage", out var value) ||
            value.ValueKind != JsonValueKind.Object) return false;
        var cost = value.TryGetProperty("cost", out var costValue) && costValue.ValueKind == JsonValueKind.Object &&
            costValue.TryGetProperty("total", out var totalCost) && totalCost.TryGetDecimal(out var parsedCost)
                ? parsedCost : 0m;
        usage = new SessionUsage(Number(value, "input"), Number(value, "output"), Number(value, "cacheRead"),
            Number(value, "cacheWrite"), cost);
        return true;
    }

    private static long Number(JsonElement value, string property) =>
        value.TryGetProperty(property, out var number) && number.TryGetInt64(out var result) ? result : 0;

    private static string? StringProperty(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) &&
        text.ValueKind == JsonValueKind.String ? text.GetString() : null;

    private readonly record struct MessageStats(int UserMessages, int AssistantMessages, int ToolCalls, int ToolResults);
    private readonly record struct SessionUsage(long Input, long Output, long CacheRead, long CacheWrite, decimal Cost);
}
