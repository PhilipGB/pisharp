using System.Text.Json;
using System.Text.Json.Nodes;
using static PiSharp.Runtime.Sessions.PiJsonlEntryCodec;

namespace PiSharp.Runtime.Sessions;

/// <summary>Projects bounded canonical run windows into Pi messages, preserving interruption and tool correlation.</summary>
internal static class PiSessionRunProjection
{
    internal static JsonArray ProjectRunMessages(ConversationSession session, string? afterEntryId, string? api,
        string? terminalType = null, string? errorMessage = null, string? throughEntryId = null)
    {
        var path = session.Tree.ActivePath();
        var firstRunEntry = 0;
        if (afterEntryId is not null)
        {
            var previousIndex = -1;
            for (var index = 0; index < path.Count; index++)
                if (path[index].Id == afterEntryId) { previousIndex = index; break; }
            if (previousIndex < 0)
                throw new InvalidOperationException("The active session branch changed during the RPC run.");
            firstRunEntry = previousIndex + 1;
        }
        var lastRunEntry = path.Count - 1;
        if (throughEntryId is not null)
        {
            lastRunEntry = -1;
            for (var index = 0; index < path.Count; index++)
                if (path[index].Id == throughEntryId) { lastRunEntry = index; break; }
            if (lastRunEntry < firstRunEntry)
                throw new InvalidOperationException("The active session branch changed during the RPC turn.");
        }
        var runPath = path.Take(lastRunEntry + 1).ToArray();

        var messages = new JsonArray();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < runPath.Length; index++)
        {
            var node = runPath[index];
            if (node.Type != "chat") continue;
            var original = OriginalEntry(node);
            JsonObject message;
            if (original is { } entry && TryProperty(entry, "message", out var originalMessage) &&
                originalMessage.ValueKind == JsonValueKind.Object)
                message = JsonNode.Parse(originalMessage.GetRawText())!.AsObject();
            else
                message = ExportNativeEntry(session, node)["message"]!.DeepClone().AsObject();

            var role = NodeString(message["role"]);
            if (role == "user" && message["content"] is JsonValue userContent &&
                userContent.TryGetValue<string>(out var userText))
                message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = userText });
            else if (role == "assistant")
            {
                if (!string.IsNullOrWhiteSpace(api)) message["api"] = api;
                if (message["content"] is JsonArray content)
                    foreach (var part in content.OfType<JsonObject>())
                        if (NodeString(part["type"]) == "toolCall" && NodeString(part["id"]) is { } callId &&
                            NodeString(part["name"]) is { } toolName)
                            toolNames[callId] = toolName;
            }
            else if (role == "toolResult" && NodeString(message["toolCallId"]) is { } resultId &&
                toolNames.TryGetValue(resultId, out var resultTool))
                message["toolName"] = resultTool;

            if (index >= firstRunEntry) messages.Add(message);
        }

        if ((terminalType is "turn_failed" or "turn_interrupted") &&
            runPath.Skip(firstRunEntry).LastOrDefault(node => node.Type == "interrupted") is { } interrupted)
        {
            var partialText = StringProperty(interrupted.Payload, "partialAssistantText") ?? "";
            var lastAssistant = messages.OfType<JsonObject>().LastOrDefault(message => NodeString(message["role"]) == "assistant");
            if (partialText.Length > 0 && lastAssistant is not null &&
                string.Equals(ReadPiText(lastAssistant["content"]), partialText, StringComparison.Ordinal))
            {
                lastAssistant["stopReason"] = terminalType == "turn_interrupted" ? "aborted" : "error";
                if (terminalType == "turn_interrupted")
                    lastAssistant["errorMessage"] = string.IsNullOrWhiteSpace(errorMessage) ? "Request was aborted" : errorMessage;
                else if (!string.IsNullOrEmpty(errorMessage))
                    lastAssistant["errorMessage"] = errorMessage;
            }
            else
            {
                var content = new JsonArray();
                if (partialText.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = partialText });
                var assistant = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = content,
                    ["timestamp"] = interrupted.Timestamp.ToUnixTimeMilliseconds(),
                    ["provider"] = session.Provider,
                    ["model"] = session.Model,
                    ["api"] = api ?? "openai-responses",
                    ["stopReason"] = terminalType == "turn_interrupted" ? "aborted" : "error",
                    ["usage"] = EmptyUsage()
                };
                if (terminalType == "turn_interrupted")
                    assistant["errorMessage"] = string.IsNullOrWhiteSpace(errorMessage) ? "Request was aborted" : errorMessage;
                else if (!string.IsNullOrEmpty(errorMessage))
                    assistant["errorMessage"] = errorMessage;
                messages.Add(assistant);
            }
        }
        return messages;
    }

    private static string ReadPiText(JsonNode? content) => content is JsonArray parts
        ? string.Concat(parts.OfType<JsonObject>().Where(part => NodeString(part["type"]) == "text")
            .Select(part => NodeString(part["text"]) ?? ""))
        : NodeString(content) ?? "";

    private static string? NodeString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

}
