using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Converts Pi records and MAF content while preserving original entries and Pi metadata.</summary>
internal static class PiJsonlEntryCodec
{
    internal static JsonObject ProjectRuntimeMessage(ConversationSession session, ChatMessage message,
        string? api = null, string? toolName = null, DateTimeOffset? timestamp = null)
    {
        var node = new ConversationNode("rpc", null, "chat", default, timestamp ?? DateTimeOffset.UtcNow);
        var projected = ExportMessage(session, node, message)["message"]!.DeepClone().AsObject();
        if (message.Role == ChatRole.Assistant && MessageProperty(message, "pisharp.api") is null &&
            !string.IsNullOrWhiteSpace(api)) projected["api"] = api;
        if (message.Role == ChatRole.Tool && toolName is not null) projected["toolName"] = toolName;
        return projected;
    }

    internal static JsonObject ProjectEntry(ConversationSession session, ConversationNode node, string? api = null)
    {
        var raw = OriginalEntry(node);
        JsonObject record;
        if (raw is { } original)
        {
            record = JsonNode.Parse(original.GetRawText())!.AsObject();
            record["id"] = node.Id;
            if (node.ParentId is not null || !TryProperty(original, "parentId", out var sourceParent) ||
                sourceParent.ValueKind == JsonValueKind.Null)
                record["parentId"] = node.ParentId;
        }
        else
        {
            record = ExportNativeEntry(session, node, api);
            record["id"] = node.Id;
            record["parentId"] = node.ParentId;
            record["timestamp"] = node.Timestamp.ToUniversalTime().ToString("O");
        }
        return record;
    }

    internal static JsonObject ProjectInterruptedMessage(ConversationSession session, ConversationNode node,
        string? api = null)
    {
        var stopReason = StringProperty(node.Payload, "stopReason") ??
            (StringProperty(node.Payload, "terminalType") == "turn_failed" ? "error" : "aborted");
        var errorMessage = StringProperty(node.Payload, "errorMessage");
        var content = new JsonArray();
        var partialText = StringProperty(node.Payload, "partialAssistantText") ?? "";
        if (partialText.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = partialText });
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content,
            ["timestamp"] = node.Timestamp.ToUnixTimeMilliseconds(),
            ["provider"] = StringProperty(node.Payload, "provider") ?? session.Provider,
            ["model"] = StringProperty(node.Payload, "model") ?? session.Model,
            ["api"] = StringProperty(node.Payload, "api") ?? api ?? "openai-responses",
            ["stopReason"] = stopReason,
            ["usage"] = EmptyUsage()
        };
        if (stopReason == "aborted") message["errorMessage"] = errorMessage ?? "Request was aborted";
        else if (errorMessage is not null) message["errorMessage"] = errorMessage;
        return message;
    }

    internal static JsonObject EmptyUsage() => new()
    {
        ["input"] = 0,
        ["output"] = 0,
        ["cacheRead"] = 0,
        ["cacheWrite"] = 0,
        ["totalTokens"] = 0,
        ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 }
    };

    internal static JsonObject ProjectToolUsage(UsageDetails usage, decimal? cost = null)
    {
        var value = new JsonObject
        {
            ["input"] = usage.InputTokenCount ?? 0,
            ["output"] = usage.OutputTokenCount ?? 0,
            ["cacheRead"] = usage.CachedInputTokenCount ?? 0,
            ["cacheWrite"] = UsageRecord.Create("tool", "tool", usage, null).CachedWriteTokens,
            ["totalTokens"] = usage.TotalTokenCount ?? (usage.InputTokenCount ?? 0) + (usage.OutputTokenCount ?? 0)
        };
        if (cost is not null) value["cost"] = new JsonObject { ["total"] = cost.Value };
        return value;
    }

    internal static JsonElement? OriginalEntry(ConversationNode node)
    {
        if (node.Type == "chat") return ConversationMessageCodec.PiEntryFromChatPayload(node.Payload);
        if (node.Type == "bash_execution") return ConversationMessageCodec.PiEntryFromBashPayload(node.Payload);
        return TryProperty(node.Payload, "piOriginalEntry", out var entry) && entry.ValueKind == JsonValueKind.Object
            ? entry.Clone() : null;
    }

    internal static ChatMessage? ImportedContextMessage(ConversationNode node)
    {
        if (node.Type == "branch_summary" && OriginalEntry(node) is { } summaryEntry &&
            StringProperty(summaryEntry, "summary") is { } summary)
            return new ChatMessage(ChatRole.User,
                $"The following is a summary of a branch that this conversation came back from:\n\n<summary>\n{summary}\n</summary>");
        if (node.Type != "custom_message" || OriginalEntry(node) is not { } custom ||
            !TryProperty(custom, "content", out var content)) return null;
        return new ChatMessage(ChatRole.User, ReadContent(content));
    }

    internal static bool TryGetContextEdit(ConversationNode node, out string targetId, out JsonElement replacement)
    {
        targetId = "";
        replacement = default;
        if (node.Type != "context_edit") return false;
        var edit = OriginalEntry(node) ?? node.Payload;
        if (StringProperty(edit, "targetId") is not { Length: > 0 } target ||
            !TryProperty(edit, "replacement", out replacement)) return false;
        targetId = target;
        return true;
    }

    internal static ChatMessage? CompactionSystemMessage(ConversationNode node)
    {
        var entry = OriginalEntry(node);
        return entry is { } original && TryProperty(original, "systemMessage", out var message)
            ? ToChatMessage(message) : null;
    }

    internal static ChatMessage? ApplyContextEdit(ConversationNode node, ChatMessage message, JsonElement replacement)
    {
        if (replacement.ValueKind == JsonValueKind.Null) return null;
        if (node.Type is not ("chat" or "custom_message") || !TryProperty(replacement, "content", out var content)) return message;
        if (node.Type == "chat" && OriginalEntry(node) is { } original && TryProperty(original, "message", out var piMessage))
            return ToChatMessage(piMessage, content);
        return new ChatMessage(message.Role, ReadContent(content));
    }

    internal static ConversationNode ToNode(JsonElement entry, string id, string? parentId, string type, DateTimeOffset timestamp)
    {
        if (type == "message" && TryProperty(entry, "message", out var message) && StringProperty(message, "role") != "bashExecution")
            return new(id, parentId, "chat", ConversationMessageCodec.EncodeMessage(ToChatMessage(message),
                NestedToolCallsFromEntry(entry), entry), timestamp);
        if (type == "message" && TryProperty(entry, "message", out message) && StringProperty(message, "role") == "bashExecution")
        {
            var record = new BashExecutionRecord(StringProperty(message, "command") ?? "",
                StringProperty(message, "output") ?? "", IntProperty(message, "exitCode"), BoolProperty(message, "cancelled"),
                BoolProperty(message, "truncated"), StringProperty(message, "fullOutputPath"), BoolProperty(message, "excludeFromContext"))
            { PiOriginalEntry = entry.Clone() };
            return new(id, parentId, "bash_execution", JsonSerializer.SerializeToElement(record), timestamp);
        }
        if (type == "compaction")
            return new(id, parentId, type, AddOriginal(entry), timestamp);
        if (type == "model_change")
        {
            var payload = new JsonObject
            {
                ["model"] = StringProperty(entry, "modelId"),
                ["provider"] = StringProperty(entry, "provider"),
                ["endpoint"] = null,
                ["piOriginalEntry"] = JsonNode.Parse(entry.GetRawText())
            };
            return new(id, parentId, type, JsonSerializer.SerializeToElement(payload), timestamp);
        }
        if (type == "custom" && StringProperty(entry, "customType") == "pi.virtual-model-state" &&
            TryProperty(entry, "data", out var virtualState) && virtualState.ValueKind == JsonValueKind.Object &&
            StringProperty(virtualState, "provider") is { Length: > 0 } virtualProvider &&
            StringProperty(virtualState, "modelId") is { Length: > 0 } virtualModelId &&
            TryProperty(virtualState, "state", out var state))
            return new(id, parentId, "virtual_model_state", JsonSerializer.SerializeToElement(new
            {
                provider = virtualProvider,
                modelId = virtualModelId,
                state,
                piOriginalEntry = entry
            }), timestamp);
        if (type is "thinking_level_change" or "branch_summary" or "custom_message" or "context_edit" or "session_info" or "label")
            return new(id, parentId, type, AddOriginal(entry), timestamp);
        return new(id, parentId, "pi_entry", JsonSerializer.SerializeToElement(new
        {
            piType = type,
            piOriginalEntry = entry
        }), timestamp);
    }

    private static JsonElement AddOriginal(JsonElement entry)
    {
        var node = JsonNode.Parse(entry.GetRawText())!.AsObject();
        node["piOriginalEntry"] = JsonNode.Parse(entry.GetRawText());
        return JsonSerializer.SerializeToElement(node);
    }

    private static ChatMessage ToChatMessage(JsonElement message, JsonElement? contentOverride = null)
    {
        var role = StringProperty(message, "role") switch
        {
            "system" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "toolResult" => ChatRole.Tool,
            _ => ChatRole.User
        };
        if (role == ChatRole.Tool)
        {
            var callId = StringProperty(message, "toolCallId") ?? "unknown-call";
            if (TryProperty(message, "piSharpToolResult", out var preserved) &&
                PiSharp.Runtime.Tools.ToolResultOutput.TryReadContract(preserved, out var contract))
                return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, contract)]);
            var toolContent = contentOverride ?? (TryProperty(message, "content", out var toolValue) ? toolValue : default);
            var toolParts = ReadContent(toolContent);
            var toolResult = new FunctionResultContent(callId, toolParts.All(part => part is TextContent)
                ? string.Join('\n', toolParts.OfType<TextContent>().Select(part => part.Text)) : toolParts);
            if (BoolProperty(message, "isError")) toolResult.Exception = new ToolFailureException("Pi session records a failed tool result.");
            return new ChatMessage(ChatRole.Tool, [toolResult]);
        }

        var content = contentOverride ?? (TryProperty(message, "content", out var messageContent) ? messageContent : default);
        var result = new ChatMessage(role, ReadContent(content, role == ChatRole.Assistant));
        if (role == ChatRole.System) SystemMessageTranscript.ImportMetadata(result, message);
        if (role == ChatRole.Assistant)
        {
            var properties = new AdditionalPropertiesDictionary();
            if (StringProperty(message, "provider") is { } provider) properties["pisharp.provider"] = provider;
            if (StringProperty(message, "model") is { } model) properties["pisharp.model"] = model;
            if (StringProperty(message, "thinkingLevel") is { } thinking) properties["pisharp.thinkingLevel"] = thinking;
            if (StringProperty(message, "api") is { } api) properties["pisharp.api"] = api;
            if (StringProperty(message, "stopReason") is { } stopReason) properties["pisharp.stopReason"] = stopReason;
            if (StringProperty(message, "errorMessage") is { } errorMessage) properties["pisharp.errorMessage"] = errorMessage;
            if (properties.Count > 0) result.AdditionalProperties = properties;
        }
        return result;
    }

    private static IList<AIContent> ReadContent(JsonElement content, bool assistant = false)
    {
        var result = new List<AIContent>();
        if (content.ValueKind == JsonValueKind.String)
        {
            result.Add(new TextContent(content.GetString() ?? ""));
            return result;
        }
        if (content.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in content.EnumerateArray())
        {
            if (StringProperty(item, "type") == "text" && StringProperty(item, "text") is { } text)
                result.Add(new TextContent(text));
            else if (assistant && StringProperty(item, "type") == "thinking" && StringProperty(item, "thinking") is { } thinking)
                result.Add(new TextReasoningContent(thinking));
            else if (assistant && StringProperty(item, "type") == "toolCall")
            {
                var args = TryProperty(item, "arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
                    ? arguments.EnumerateObject().ToDictionary(property => property.Name, property => ToObject(property.Value), StringComparer.Ordinal)
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                result.Add(new FunctionCallContent(StringProperty(item, "id") ?? "unknown-call",
                    StringProperty(item, "name") ?? "unknown-tool", args));
            }
            else if (StringProperty(item, "type") == "image" && StringProperty(item, "data") is { } data &&
                StringProperty(item, "mimeType") is { } mimeType)
            {
                try { result.Add(new DataContent(Convert.FromBase64String(data), mimeType)); }
                catch (FormatException) { result.Add(new TextContent("[invalid Pi image content omitted]")); }
            }
        }
        return result;
    }

    internal static JsonObject ExportNativeEntry(ConversationSession session, ConversationNode node, string? api = null)
    {
        if (node.Type == "interrupted")
            return new JsonObject { ["type"] = "message", ["message"] = ProjectInterruptedMessage(session, node, api) };
        if (node.Type == "chat")
        {
            var messageElement = node.Payload.GetProperty("Message");
            var message = messageElement.Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidDataException($"Missing message at {node.Id}.");
            return ExportMessage(session, node, message);
        }
        if (node.Type == "bash_execution")
        {
            var record = node.Payload.Deserialize<BashExecutionRecord>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException($"Missing Bash execution at {node.Id}.");
            return new JsonObject
            {
                ["type"] = "message",
                ["message"] = new JsonObject
                {
                    ["role"] = "bashExecution",
                    ["command"] = record.Command,
                    ["output"] = record.Output,
                    ["exitCode"] = record.ExitCode,
                    ["cancelled"] = record.Cancelled,
                    ["truncated"] = record.Truncated,
                    ["fullOutputPath"] = record.FullOutputPath,
                    ["excludeFromContext"] = record.ExcludeFromContext,
                    ["timestamp"] = node.Timestamp.ToUnixTimeMilliseconds()
                }
            };
        }
        if (node.Type == "compaction")
        {
            var payload = node.Payload;
            var firstKept = StringProperty(payload, "firstKeptEntryId");
            var entry = new JsonObject
            {
                ["type"] = "compaction",
                ["summary"] = StringProperty(payload, "summary"),
                ["firstKeptEntryId"] = firstKept,
                ["tokensBefore"] = IntProperty(payload, "tokensBefore") ?? 0
            };
            if (TryProperty(payload, "details", out var details) && details.ValueKind == JsonValueKind.Object)
                entry["details"] = JsonNode.Parse(details.GetRawText());
            return entry;
        }
        if (node.Type == "model_change")
            return new JsonObject
            {
                ["type"] = "model_change",
                ["provider"] = StringProperty(node.Payload, "provider"),
                ["modelId"] = StringProperty(node.Payload, "model"),
                ["endpoint"] = StringProperty(node.Payload, "endpoint")
            };
        if (node.Type == "thinking_level_change")
            return new JsonObject
            {
                ["type"] = "thinking_level_change",
                ["thinkingLevel"] = StringProperty(node.Payload, "thinkingLevel") ??
                    throw new InvalidDataException($"Missing thinking level at {node.Id}.")
            };
        if (node.Type == "session_info")
            return new JsonObject
            {
                ["type"] = "session_info",
                ["name"] = StringProperty(node.Payload, "name") ??
                    throw new InvalidDataException($"Missing session name at {node.Id}.")
            };
        if (node.Type == "virtual_model_state")
            return new JsonObject
            {
                ["type"] = "custom",
                ["customType"] = "pi.virtual-model-state",
                ["data"] = new JsonObject
                {
                    ["provider"] = StringProperty(node.Payload, "provider"),
                    ["modelId"] = StringProperty(node.Payload, "modelId"),
                    ["state"] = JsonNode.Parse(node.Payload.GetProperty("state").GetRawText())
                }
            };
        if (node.Type == "context_edit")
        {
            if (!TryGetContextEdit(node, out var targetId, out var replacement))
                throw new InvalidDataException($"Invalid context edit at {node.Id}.");
            return new JsonObject
            {
                ["type"] = "context_edit",
                ["targetId"] = targetId,
                ["replacement"] = JsonNode.Parse(replacement.GetRawText())
            };
        }
        return new JsonObject
        {
            ["type"] = "custom",
            ["customType"] = "pisharp." + node.Type,
            ["data"] = JsonNode.Parse(node.Payload.GetRawText())
        };
    }

    private static JsonObject ExportMessage(ConversationSession session, ConversationNode node, ChatMessage message)
    {
        var role = message.Role == ChatRole.Assistant ? "assistant" : message.Role == ChatRole.System ? "system" :
            message.Role == ChatRole.Tool ? "toolResult" : "user";
        var content = new JsonArray();
        foreach (var part in message.Contents)
        {
            switch (part)
            {
                case TextContent text:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case TextReasoningContent reasoning:
                    content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.Text });
                    break;
                case DataContent image:
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = Convert.ToBase64String(image.Data.ToArray()),
                        ["mimeType"] = image.MediaType
                    });
                    break;
                case FunctionCallContent call:
                    content.Add(new JsonObject
                    {
                        ["type"] = "toolCall",
                        ["id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = JsonSerializer.SerializeToNode(call.Arguments)
                    });
                    break;
                case FunctionResultContent result:
                    var resultText = ToolResultOutput.TryRead(result.Result, out var structuredText, out _)
                        ? structuredText : result.Result is string value ? value : result.Result?.ToString() ?? "";
                    if (role == "toolResult") content.Add(new JsonObject { ["type"] = "text", ["text"] = resultText });
                    if (role == "toolResult" && ToolResultOutput.TryReadContract(result.Result, out var toolOutput) &&
                        toolOutput.Images is { Count: > 0 })
                        foreach (var image in toolOutput.Images)
                            content.Add(new JsonObject
                            {
                                ["type"] = "image",
                                ["mimeType"] = image.MimeType,
                                ["data"] = image.DataBase64
                            });
                    break;
            }
        }
        JsonNode contentNode = content;
        if (role is "user" or "system" && content.Count == 1 && content[0]?["type"]?.GetValue<string>() == "text")
            contentNode = JsonValue.Create(content[0]?["text"]?.GetValue<string>())!;
        var exported = new JsonObject
        {
            ["type"] = "message",
            ["message"] = new JsonObject
            {
                ["role"] = role,
                ["content"] = contentNode,
                ["timestamp"] = node.Timestamp.ToUnixTimeMilliseconds()
            }
        };
        var messageObject = exported["message"]!.AsObject();
        if (role == "system") SystemMessageTranscript.ExportMetadata(message, messageObject);
        if (role == "assistant")
        {
            messageObject["provider"] = MessageProperty(message, "pisharp.provider") ?? session.Provider;
            messageObject["model"] = MessageProperty(message, "pisharp.model") ?? session.Model;
            messageObject["thinkingLevel"] = MessageProperty(message, "pisharp.thinkingLevel");
            messageObject["api"] = MessageProperty(message, "pisharp.api") ?? "openai-responses";
            messageObject["stopReason"] = message.Contents.OfType<FunctionCallContent>().Any() ? "toolUse" : "stop";
            messageObject["usage"] = EmptyUsage();
        }
        if (role == "toolResult")
        {
            var result = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            messageObject["toolCallId"] = result?.CallId ?? "unknown-call";
            messageObject["toolName"] = "tool";
            PiSharpToolResult? contract = null;
            if (result is not null && ToolResultOutput.TryReadContract(result.Result, out var parsed)) contract = parsed;
            messageObject["isError"] = result?.Exception is not null || contract?.IsError == true;
            if (contract is not null) messageObject["piSharpToolResult"] = JsonSerializer.SerializeToNode(contract);
            if (contract?.Usage is { } toolUsage) messageObject["usage"] = ProjectToolUsage(toolUsage, contract.Cost);
            if (contract?.Terminate == true) messageObject["terminate"] = true;
        }
        if (ConversationMessageCodec.NestedToolCallsFor(node) is { } nestedToolCalls)
            messageObject["nestedCalls"] = JsonSerializer.SerializeToNode(nestedToolCalls);
        return exported;
    }

    private static string? MessageProperty(ChatMessage message, string name) =>
        ChatMessageProperties.String(message.AdditionalProperties, name);

    private static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToArray(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name,
            property => ToObject(property.Value), StringComparer.Ordinal),
        _ => null
    };

    internal static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    internal static string? StringProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static PiSharpNestedToolCalls? NestedToolCallsFromEntry(JsonElement entry)
    {
        if (!TryProperty(entry, "message", out var message) ||
            !TryProperty(message, "nestedCalls", out var nestedCalls) ||
            nestedCalls.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        try { return nestedCalls.Deserialize<PiSharpNestedToolCalls>(); }
        catch (JsonException error) { throw new InvalidDataException("Invalid nested tool-call metadata.", error); }
    }

    private static int? IntProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.TryGetInt32(out var integer) ? integer : null;

    private static bool BoolProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.True;

    internal static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : DateTimeOffset.UnixEpoch;

}
