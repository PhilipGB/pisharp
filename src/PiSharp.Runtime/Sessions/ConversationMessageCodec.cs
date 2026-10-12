using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Encodes native chat/tool payloads and restores explicit failure and nested-call state.</summary>
internal static class ConversationMessageCodec
{
    private static readonly JsonSerializerOptions BashExecutionJsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonElement EncodeMessage(ChatMessage message, PiSharpNestedToolCalls? nestedToolCalls = null,
        JsonElement? originalEntry = null)
    {
        var errors = message.Contents.Select((content, index) => new { content, index })
            .Where(item => item.content is FunctionResultContent { Exception: not null } or FunctionCallContent { Exception: not null })
            .Select(item => new ToolError(item.index, item.content is FunctionResultContent ? "result" : "call",
                item.content is FunctionResultContent result ? result.Exception!.Message : ((FunctionCallContent)item.content).Exception!.Message))
            .ToArray();
        return JsonSerializer.SerializeToElement(new ChatRecord(
            JsonSerializer.SerializeToElement(message, AIJsonUtilities.DefaultOptions), errors,
            originalEntry?.Clone(), nestedToolCalls));
    }

    public static JsonElement EncodeLegacyMessage(JsonElement message) =>
        JsonSerializer.SerializeToElement(new ChatRecord(message, []));

    internal static JsonElement? PiEntryFromChatPayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("PiEntry", out var entry) &&
        entry.ValueKind == JsonValueKind.Object ? entry.Clone() : null;

    internal static JsonElement? PiEntryFromBashPayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("piOriginalEntry", out var entry) &&
        entry.ValueKind == JsonValueKind.Object ? entry.Clone() : null;

    internal static PiSharpNestedToolCalls? NestedToolCallsFor(ConversationNode node)
    {
        if (node.Type != "chat" || node.Payload.ValueKind != JsonValueKind.Object) return null;
        return node.Payload.Deserialize<ChatRecord>()?.NestedToolCalls;
    }

    internal static ChatMessage RestoreEntry(ConversationNode entry) => entry.Type == "chat"
        ? Restore(entry.Payload, entry.Id) : throw new ArgumentException("Not a chat entry.", nameof(entry));

    internal static BashExecutionRecord RestoreBashExecution(JsonElement payload, string id)
    {
        if (!payload.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String ||
            command.GetString() is null ||
            !payload.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.String ||
            !payload.TryGetProperty("exitCode", out var exitCode) ||
            (exitCode.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number) ||
                exitCode.ValueKind == JsonValueKind.Number && !exitCode.TryGetInt32(out _)) ||
            !payload.TryGetProperty("cancelled", out var cancelled) || cancelled.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !payload.TryGetProperty("truncated", out var truncated) || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !payload.TryGetProperty("fullOutputPath", out var fullOutputPath) ||
                (fullOutputPath.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) ||
            !payload.TryGetProperty("excludeFromContext", out var excludeFromContext) ||
                excludeFromContext.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Invalid Bash execution at {id}.");
        var execution = payload.Deserialize<BashExecutionRecord>(BashExecutionJsonOptions);
        if (execution is null)
            throw new InvalidDataException($"Invalid Bash execution at {id}.");
        return execution;
    }

    internal static ChatMessage Restore(JsonElement payload, string id)
    {
        var record = payload.Deserialize<ChatRecord>() ?? throw new InvalidDataException($"Missing message at {id}.");
        if (record.Message.ValueKind != JsonValueKind.Object || record.Errors is null)
            throw new InvalidDataException($"Incomplete chat record at {id}.");
        var message = record.Message.Deserialize<ChatMessage>(AIJsonUtilities.DefaultOptions)
            ?? throw new InvalidDataException($"Missing message at {id}.");
        StoredToolResultReplay.Restore(message);
        foreach (var error in record.Errors)
        {
            if (error.Index < 0 || error.Index >= message.Contents.Count) throw new InvalidDataException($"Invalid tool failure at {id}.");
            var exception = new ToolFailureException(error.Message);
            if (error.Type == "result" && message.Contents[error.Index] is FunctionResultContent result) result.Exception = exception;
            else if (error.Type == "call" && message.Contents[error.Index] is FunctionCallContent call) call.Exception = exception;
            else throw new InvalidDataException($"Invalid tool failure kind at {id}.");
        }
        return message;
    }

    private sealed record ChatRecord(JsonElement Message, ToolError[]? Errors, JsonElement? PiEntry = null,
        [property: JsonPropertyName("nestedCalls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        PiSharpNestedToolCalls? NestedToolCalls = null);

    private sealed record ToolError(int Index, string Type, string Message);

    public static JsonElement EncodeBashExecution(BashExecutionRecord execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (execution.Command is null || execution.Output is null)
            throw new InvalidDataException("A Bash execution requires a command and output.");
        return JsonSerializer.SerializeToElement(execution, BashExecutionJsonOptions);
    }
}
