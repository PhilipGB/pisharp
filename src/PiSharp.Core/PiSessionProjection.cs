using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Core;

/// <summary>Model-visible Pi messages for one branch. Raw journal entries remain unchanged.</summary>
public sealed record PiSessionContext(IReadOnlyList<JsonElement> Messages, string ThinkingLevel, string? Provider, string? ModelId);

public static class PiSessionProjection
{
    public static PiSessionContext Build(ConversationTree tree)
    {
        var path = tree.ActivePath().ToList();
        string thinking = "off";
        string? provider = null, model = null;
        foreach (var entry in path)
        {
            var data = entry.Payload;
            if (entry.Type == "thinking_level_change") thinking = data.GetProperty("thinkingLevel").GetString()!;
            if (entry.Type == "model_change")
            {
                provider = data.GetProperty("provider").GetString();
                model = data.GetProperty("modelId").GetString();
            }
            if (entry.Type == "message" && data.GetProperty("message").GetProperty("role").GetString() == "assistant")
            {
                var message = data.GetProperty("message");
                provider = message.GetProperty("provider").GetString();
                model = message.GetProperty("model").GetString();
            }
        }

        var latestCompaction = path.LastOrDefault(e => e.Type == "compaction");
        var selected = new List<ConversationNode>();
        if (latestCompaction is null) selected.AddRange(path);
        else
        {
            selected.Add(latestCompaction);
            var index = path.IndexOf(latestCompaction);
            var firstKept = latestCompaction.Payload.GetProperty("firstKeptEntryId").GetString();
            var keeping = false;
            for (var i = 0; i < index; i++)
            {
                var entry = path[i];
                if (entry.Id == firstKept) keeping = true;
                if (keeping && !(entry.Type == "message" && entry.Payload.GetProperty("message").GetProperty("role").GetString() == "system"))
                    selected.Add(entry);
            }
            selected.AddRange(path.Skip(index + 1));
        }
        var edits = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in selected)
            if (entry.Type == "context_edit") edits[entry.Payload.GetProperty("targetId").GetString()!] = entry.Payload.GetProperty("replacement");
        var messages = new List<JsonElement>();
        for (var i = 0; i < selected.Count; i++)
        {
            var entry = selected[i];
            var payload = entry.Payload;
            JsonElement? message = entry.Type switch
            {
                "message" => payload.GetProperty("message"),
                "compaction" when i == 0 => AddCompaction(messages, entry),
                "branch_summary" => JsonSerializer.SerializeToElement(new { role = "branchSummary", summary = payload.GetProperty("summary").GetString(), fromId = payload.GetProperty("fromId").GetString(), timestamp = entry.Timestamp.ToUnixTimeMilliseconds() }),
                "custom_message" => CustomMessage(entry),
                _ => null
            };
            if (message is null) continue;
            var visible = message.Value;
            if (entry.Type == "message" && visible.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Null)
            {
                var role = visible.GetProperty("role").GetString();
                visible = ReplaceContent(visible, JsonSerializer.SerializeToElement(role == "system" ? (object)"" : Array.Empty<object>()));
            }
            if (edits.TryGetValue(entry.Id, out var edit))
            {
                if (edit.ValueKind == JsonValueKind.Null) continue;
                var role = visible.GetProperty("role").GetString();
                if (role is "user" or "assistant" or "toolResult" or "custom")
                {
                    var replacement = edit.GetProperty("content");
                    if (role is "assistant" or "toolResult" && replacement.ValueKind == JsonValueKind.String)
                        replacement = JsonSerializer.SerializeToElement(new[] { new { type = "text", text = replacement.GetString() } });
                    visible = ReplaceContent(visible, replacement);
                }
            }
            messages.Add(visible);
        }
        return new PiSessionContext(messages, thinking, provider, model);
    }

    private static JsonElement AddCompaction(List<JsonElement> output, ConversationNode entry)
    {
        var data = entry.Payload;
        if (data.TryGetProperty("systemMessage", out var system)) output.Add(system.Clone());
        return JsonSerializer.SerializeToElement(new { role = "compactionSummary", summary = data.GetProperty("summary").GetString(), tokensBefore = data.GetProperty("tokensBefore").GetInt32(), timestamp = entry.Timestamp.ToUnixTimeMilliseconds() });
    }

    private static JsonElement CustomMessage(ConversationNode entry)
    {
        var data = entry.Payload;
        var node = new JsonObject
        {
            ["role"] = "custom",
            ["customType"] = data.GetProperty("customType").GetString(),
            ["content"] = data.TryGetProperty("content", out var content) ? JsonNode.Parse(content.GetRawText()) : new JsonArray(),
            ["display"] = data.GetProperty("display").GetBoolean(),
            ["timestamp"] = entry.Timestamp.ToUnixTimeMilliseconds()
        };
        if (data.TryGetProperty("details", out var details)) node["details"] = JsonNode.Parse(details.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }

    private static JsonElement ReplaceContent(JsonElement message, JsonElement content)
    {
        var node = JsonNode.Parse(message.GetRawText())!.AsObject();
        node["content"] = JsonNode.Parse(content.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }
}
