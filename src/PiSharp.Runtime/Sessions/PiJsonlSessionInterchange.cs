using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Reads and writes the current Pi JSONL session tree while PiSharp keeps its own runtime format.</summary>
public static class PiJsonlSessionInterchange
{
    private const int MaximumFileBytes = 128 * 1024 * 1024;
    private const int MaximumEntryBytes = 16 * 1024 * 1024;

    /// <summary>Imports Pi v1-v3 JSONL, migrating legacy links and retaining original Pi records for export.</summary>
    public static ConversationSession Import(string jsonl, string? workingDirectoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(jsonl);
        if (Encoding.UTF8.GetByteCount(jsonl) > MaximumFileBytes)
            throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");

        var records = ParseRecords(jsonl);
        if (records.Count == 0 || StringProperty(records[0], "type") != "session")
            throw new InvalidDataException("The file does not begin with a Pi session header.");
        var header = records[0];
        var sessionId = StringProperty(header, "id");
        var cwd = workingDirectoryOverride ?? StringProperty(header, "cwd");
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(cwd))
            throw new InvalidDataException("The Pi session header must contain a session id and working directory.");
        cwd = Path.GetFullPath(cwd);

        var version = 1;
        if (TryProperty(header, "version", out var versionValue))
        {
            if (versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out version))
                throw new InvalidDataException("Pi session version must be an integer.");
        }
        if (version is < 1 or > 3)
            throw new InvalidDataException($"Pi session version {version} is not supported.");

        var entries = records.Skip(1).Select(record => JsonNode.Parse(record.GetRawText())!.AsObject()).ToArray();
        if (version == 1) MigrateV1(entries);
        if (version < 3)
            foreach (var entry in entries) MigrateHookMessage(entry);

        var nodes = new List<ConversationNode>(entries.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mutableEntry in entries)
        {
            var entry = JsonSerializer.SerializeToElement(mutableEntry);
            var id = StringProperty(entry, "id");
            var type = StringProperty(entry, "type");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) || !seen.Add(id))
                throw new InvalidDataException("Pi session entries must have unique ids and a type.");
            var parentId = StringProperty(entry, "parentId");
            if (parentId is not null && !seen.Contains(parentId))
                throw new InvalidDataException($"Pi session entry {id} refers to a missing or forward parent {parentId}.");
            var timestamp = ParseTimestamp(StringProperty(entry, "timestamp"));
            nodes.Add(ToNode(entry, id, parentId, type, timestamp));
        }

        var tree = new ConversationTree(nodes);
        var activePath = tree.ActivePath();
        var model = "unknown";
        string? provider = null;
        string? name = null;
        foreach (var node in activePath)
        {
            if (node.Type == "model_change")
            {
                var source = OriginalEntry(node);
                if (source is { } modelEntry)
                {
                    model = StringProperty(modelEntry, "modelId") ?? model;
                    provider = StringProperty(modelEntry, "provider") ?? provider;
                }
            }
            else if (node.Type == "chat" && OriginalEntry(node) is { } messageEntry &&
                TryProperty(messageEntry, "message", out var message) && StringProperty(message, "role") == "assistant")
            {
                model = StringProperty(message, "model") ?? model;
                provider = StringProperty(message, "provider") ?? provider;
            }
            else if (node.Type == "session_info" && OriginalEntry(node) is { } info)
            {
                name = TryProperty(info, "name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(nameValue.GetString()) ? nameValue.GetString()!.Trim() : null;
            }
        }

        var session = ConversationSession.FromPiJsonl(sessionId, cwd, model, provider, name, tree, header);
        _ = session.ActiveMessages();
        _ = session.ContextMessages();
        return session;
    }

    /// <summary>Serializes all branches as current Pi v3 JSONL; unknown imported records are preserved verbatim.</summary>
    public static string Export(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var output = new StringBuilder();
        var header = session.PiJsonlHeader is { } sourceHeader && sourceHeader.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(sourceHeader.GetRawText())!.AsObject()
            : new JsonObject();
        header["type"] = "session";
        header["version"] = 3;
        header["id"] = session.Id;
        header["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        header["cwd"] = session.WorkingDirectory;
        if (session.ParentSessionPath is not null) header["parentSession"] = session.ParentSessionPath;
        AppendLine(output, header);

        var orderedEntries = OrderedEntriesForExport(session);
        foreach (var node in orderedEntries)
            AppendLine(output, ProjectEntry(session, node));

        var activePath = session.Tree.ActivePath();
        var lastInfo = activePath.LastOrDefault(node => node.Type == "session_info");
        var representedName = lastInfo is not null && OriginalEntry(lastInfo) is { } nameEntry
            ? StringProperty(nameEntry, "name") : null;
        var needsHeadAnchor = orderedEntries.LastOrDefault()?.Id != session.Tree.HeadId;
        if (needsHeadAnchor || session.Name != representedName)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var info = new JsonObject
            {
                ["type"] = "session_info",
                ["id"] = id,
                ["parentId"] = session.Tree.HeadId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            };
            if (session.Name is not null) info["name"] = session.Name;
            AppendLine(output, info);
        }
        return output.ToString();
    }

    /// <summary>Projects stored entries to Pi v3 entry objects without adding a session header or branch anchor.</summary>
    public static IReadOnlyList<JsonElement> ProjectEntries(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Tree.Entries.Select(node =>
            JsonSerializer.SerializeToElement(ProjectEntry(session, node))).ToArray();
    }

    private static JsonObject ProjectEntry(ConversationSession session, ConversationNode node)
    {
        var raw = OriginalEntry(node);
        JsonObject record;
        if (raw is { } original)
        {
            record = JsonNode.Parse(original.GetRawText())!.AsObject();
            record["id"] = node.Id;
            record["parentId"] = node.ParentId;
        }
        else
        {
            record = ExportNativeEntry(session, node);
            record["id"] = node.Id;
            record["parentId"] = node.ParentId;
            record["timestamp"] = node.Timestamp.ToUniversalTime().ToString("O");
        }
        return record;
    }

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
            if (role == "assistant")
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
                if (terminalType == "turn_failed" && !string.IsNullOrEmpty(errorMessage))
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
                if (terminalType == "turn_failed" && !string.IsNullOrEmpty(errorMessage))
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

    private static JsonObject EmptyUsage() => new()
    {
        ["input"] = 0,
        ["output"] = 0,
        ["cacheRead"] = 0,
        ["cacheWrite"] = 0,
        ["totalTokens"] = 0,
        ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 }
    };

    /// <summary>Writes Pi JSONL without replacing an existing export; files are user-private on Unix.</summary>
    public static async Task ExportToFileAsync(ConversationSession session, string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(directory);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var bytes = Encoding.UTF8.GetBytes(Export(session));
        var created = false;
        try
        {
            await using var file = new FileStream(target, options);
            created = true;
            await file.WriteAsync(bytes, cancellationToken);
            file.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(target); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>Resolve the current branch's Pi thinking-level state, if the imported log records one.</summary>
    public static string? GetThinkingLevel(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Tree.ActivePath().Where(node => node.Type == "thinking_level_change")
            .Select(node => StringProperty(node.Payload, "thinkingLevel") ??
                (OriginalEntry(node) is { } entry ? StringProperty(entry, "thinkingLevel") : null))
            .LastOrDefault(level => level is not null);
    }

    internal static JsonElement? OriginalEntry(ConversationNode node)
    {
        if (node.Type == "chat") return ConversationSession.PiEntryFromChatPayload(node.Payload);
        if (node.Type == "bash_execution") return ConversationSession.PiEntryFromBashPayload(node.Payload);
        return TryProperty(node.Payload, "piOriginalEntry", out var entry) && entry.ValueKind == JsonValueKind.Object
            ? entry.Clone() : null;
    }

    private static IReadOnlyList<ConversationNode> OrderedEntriesForExport(ConversationSession session)
    {
        var entries = session.Tree.Entries;
        var activeIds = session.Tree.ActivePath().Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ConversationNode>(entries.Count);
        while (ordered.Count < entries.Count)
        {
            var next = entries.FirstOrDefault(node => !emitted.Contains(node.Id) &&
                (node.ParentId is null || emitted.Contains(node.ParentId)) && !activeIds.Contains(node.Id));
            next ??= entries.FirstOrDefault(node => !emitted.Contains(node.Id) &&
                (node.ParentId is null || emitted.Contains(node.ParentId)));
            if (next is null)
                throw new InvalidDataException("Session tree cannot be written in parent-before-child order.");
            ordered.Add(next);
            emitted.Add(next.Id);
        }
        return ordered;
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

    private static List<JsonElement> ParseRecords(string text)
    {
        var records = new List<JsonElement>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (Encoding.UTF8.GetByteCount(line) > MaximumEntryBytes) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    TryProperty(document.RootElement, "type", out _))
                    records.Add(document.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        return records;
    }

    private static void MigrateV1(JsonObject[] entries)
    {
        foreach (var entry in entries)
            entry["id"] = Guid.NewGuid().ToString("N")[..8];

        string? previous = null;
        foreach (var entry in entries)
        {
            entry["parentId"] = previous;
            previous = NodeString(entry, "id");
        }

        foreach (var entry in entries)
        {
            if (NodeString(entry, "type") == "compaction" && entry["firstKeptEntryIndex"] is JsonValue indexNode &&
                indexNode.TryGetValue<int>(out var firstKeptIndex) && firstKeptIndex > 0 && firstKeptIndex <= entries.Length)
                entry["firstKeptEntryId"] = NodeString(entries[firstKeptIndex - 1], "id");
            if (NodeString(entry, "type") == "compaction") entry.Remove("firstKeptEntryIndex");
        }
    }

    private static void MigrateHookMessage(JsonObject entry)
    {
        if (NodeString(entry, "type") == "message" && entry["message"] is JsonObject message &&
            NodeString(message, "role") == "hookMessage") message["role"] = "custom";
    }

    private static string? NodeString(JsonObject value, string key) => value[key] is JsonValue scalar &&
        scalar.TryGetValue<string>(out var text) ? text : null;

    private static ConversationNode ToNode(JsonElement entry, string id, string? parentId, string type, DateTimeOffset timestamp)
    {
        if (type == "message" && TryProperty(entry, "message", out var message) && StringProperty(message, "role") != "bashExecution")
            return ConversationSession.ImportedPiMessage(id, parentId, timestamp, ToChatMessage(message), entry);
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
            var toolContent = contentOverride ?? (TryProperty(message, "content", out var toolValue) ? toolValue : default);
            var toolResult = new FunctionResultContent(callId, ReadContent(toolContent));
            if (BoolProperty(message, "isError")) toolResult.Exception = new ToolFailureException("Pi session records a failed tool result.");
            return new ChatMessage(ChatRole.Tool, [toolResult]);
        }

        var content = contentOverride ?? (TryProperty(message, "content", out var messageContent) ? messageContent : default);
        return new ChatMessage(role, ReadContent(content, role == ChatRole.Assistant));
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

    private static JsonObject ExportNativeEntry(ConversationSession session, ConversationNode node)
    {
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
            return new JsonObject
            {
                ["type"] = "compaction",
                ["summary"] = StringProperty(payload, "summary"),
                ["firstKeptEntryId"] = firstKept,
                ["tokensBefore"] = IntProperty(payload, "tokensBefore") ?? 0
            };
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
                    var resultText = result.Result is string value ? value : result.Result?.ToString() ?? "";
                    if (role == "toolResult") content.Add(new JsonObject { ["type"] = "text", ["text"] = resultText });
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
        if (role == "assistant")
        {
            messageObject["provider"] = session.Provider;
            messageObject["model"] = session.Model;
            messageObject["api"] = "openai-responses";
            messageObject["stopReason"] = message.Contents.OfType<FunctionCallContent>().Any() ? "toolUse" : "stop";
            messageObject["usage"] = EmptyUsage();
        }
        if (role == "toolResult")
        {
            var result = message.Contents.OfType<FunctionResultContent>().FirstOrDefault();
            messageObject["toolCallId"] = result?.CallId ?? "unknown-call";
            messageObject["toolName"] = "tool";
            messageObject["isError"] = result?.Exception is not null;
        }
        return exported;
    }

    private static void AppendLine(StringBuilder output, JsonObject record) => output.Append(record.ToJsonString()).Append('\n');

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

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static string? StringProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.TryGetInt32(out var integer) ? integer : null;

    private static bool BoolProperty(JsonElement element, string name) =>
        TryProperty(element, name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? NodeString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static DateTimeOffset ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : DateTimeOffset.UnixEpoch;
}
