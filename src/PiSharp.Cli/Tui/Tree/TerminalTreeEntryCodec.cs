using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Decodes native session entries into the facts the tree navigator renders and filters.</summary>
internal sealed class TerminalTreeEntryCodec
{
    private readonly Dictionary<string, (string Name, IDictionary<string, object?> Arguments)> _toolCalls = new(StringComparer.Ordinal);

    /// <summary>Builds the tool-call lookup used to render tool results with their call identity.</summary>
    public void ObserveAssistantCalls(ConversationNode node)
    {
        if (node.Type != "chat") return;
        ChatMessage message;
        try { message = ConversationMessageCodec.RestoreEntry(node); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { return; }
        if (message.Role != ChatRole.Assistant) return;
        foreach (var call in message.Contents.OfType<FunctionCallContent>())
            _toolCalls[call.CallId] = (call.Name, call.Arguments ?? new Dictionary<string, object?>());
    }

    public TerminalTreeEntryInfo Decode(ConversationNode node)
    {
        try
        {
            return DecodeCore(node);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // A malformed entry must not take down the navigator; show its type instead.
            var fallback = new TerminalTreeEntryInfo
            {
                DisplayKind = "unknown",
                Prefix = "",
                Text = $"[{TerminalTreeText.Line(node.Type)}]",
                SearchableText = $"{node.Type}",
                IsBookkeeping = IsBookkeepingType(node.Type)
            };
            return fallback;
        }
    }

    private TerminalTreeEntryInfo DecodeCore(ConversationNode node) => node.Type switch
    {
        "chat" => DecodeChat(node),
        "bash_execution" => DecodeBashExecution(node),
        "custom_message" => DecodeCustomMessage(node),
        "compaction" => DecodeCompaction(node),
        "branch_summary" => DecodeBranchSummary(node),
        "model_change" => DecodePrefix(node, "model_change", "model: ",
            PayloadString(node.Payload, "model"), $"model {PayloadString(node.Payload, "model") ?? ""}", isSettingsEntry: true),
        "thinking_level_change" => DecodePrefix(node, "thinking_level_change", "thinking: ",
            PayloadString(node.Payload, "thinkingLevel"), $"thinking {PayloadString(node.Payload, "thinkingLevel") ?? ""}", isSettingsEntry: true),
        "context_edit" => DecodePrefix(node, "context_edit",
            $"context {(ReplacementIsNull(node.Payload) ? "omit" : "replace")}: ",
            PayloadString(node.Payload, "targetId"),
            $"context edit {(ReplacementIsNull(node.Payload) ? "omit" : "replace")} {PayloadString(node.Payload, "targetId") ?? ""}", isSettingsEntry: true),
        "label" => DecodePrefix(node, "label", "label: ",
            PayloadString(node.Payload, "label") ?? "(cleared)",
            $"label {PayloadString(node.Payload, "label") ?? ""}", isSettingsEntry: true),
        "session_info" => DecodePrefix(node, "session_info", "title: ",
            PayloadString(node.Payload, "name") ?? "empty",
            PayloadString(node.Payload, "name") is { } sessionName ? $"title {sessionName}" : "title", isSettingsEntry: true),
        "extension_state" => DecodePrefix(node, "custom", "custom: ",
            PayloadString(node.Payload, "customType"),
            $"custom {PayloadString(node.Payload, "customType") ?? ""}", isSettingsEntry: true),
        "usage" or "tool_loadout" or "run_started" or "run_finished" or "run_recovered" or
            "tool_intent" or "tool_outcome" or "tool_skipped" or "assistant_progress" or
            "context_projection" or "tool_transcript" or "interrupted" => new TerminalTreeEntryInfo
            {
                DisplayKind = "internal",
                Prefix = "",
                Text = $"[{node.Type}]",
                SearchableText = node.Type,
                IsBookkeeping = true,
                IsSettingsEntry = true
            },
        _ => new TerminalTreeEntryInfo
        {
            DisplayKind = "unknown",
            Prefix = "",
            Text = $"[{TerminalTreeText.Line(node.Type)}]",
            SearchableText = node.Type,
            IsBookkeeping = IsBookkeepingType(node.Type)
        }
    };

    private static bool IsBookkeepingType(string type) =>
        type is "usage" or "tool_loadout" or "run_started" or "run_finished" or "run_recovered" or
            "tool_intent" or "tool_outcome" or "tool_skipped" or "assistant_progress" or
            "context_projection" or "tool_transcript" or "interrupted";

    private TerminalTreeEntryInfo DecodeChat(ConversationNode node)
    {
        var message = ConversationMessageCodec.RestoreEntry(node);
        var role = message.Role;
        var text = string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));
        var hasText = text.Trim().Length > 0;
        var stopReason = Additional(message, "pisharp.stopReason");
        var errorMessage = Additional(message, "pisharp.errorMessage");
        if (role == ChatRole.Tool)
        {
            var callId = message.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId;
            var toolName = TryString(PiEntry(node), "toolName");
            var display = callId is not null && _toolCalls.TryGetValue(callId, out var call)
                ? FormatToolCall(call.Name, call.Arguments)
                : $"[{toolName ?? "tool"}]";
            var toolResultCopy = text.Length > 0 ? ExtractFullContent(message) : null;
            return new TerminalTreeEntryInfo
            {
                DisplayKind = "toolResult",
                Prefix = "",
                Text = display,
                SearchableText = Searchable($"{RoleName(message.Role)} {Clip(ExtractFullContent(message), 200)}"),
                CopyText = toolResultCopy,
                IsToolResult = true,
                HasText = hasText,
                IsBookkeeping = false
            };
        }
        if (role == ChatRole.User || role == ChatRole.Assistant || role == ChatRole.System)
        {
            var prefix = role == ChatRole.User ? "user: " : role == ChatRole.Assistant ? "assistant: " : "";
            string display = text;
            string? placeholder = null;
            if (role == ChatRole.Assistant && !hasText)
            {
                if (stopReason == "aborted") { display = "(aborted)"; placeholder = "aborted"; }
                else if (errorMessage is { Length: > 0 } error) { display = Clip(Normalize(error), 80); placeholder = "error"; }
                else { display = "(no content)"; placeholder = "no-content"; }
            }
            var fullText = ExtractFullContent(message);
            var copyText = fullText.Trim().Length > 0 ? fullText
                : role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(errorMessage) ? errorMessage
                : null;
            var isErrorOrAborted = stopReason is { Length: > 0 } &&
                !stopReason.Equals("stop", StringComparison.OrdinalIgnoreCase) &&
                !stopReason.Equals("toolUse", StringComparison.OrdinalIgnoreCase);
            return new TerminalTreeEntryInfo
            {
                DisplayKind = role == ChatRole.User ? "user" : role == ChatRole.Assistant ? "assistant" : "system",
                Prefix = prefix,
                Text = Normalize(
                    role == ChatRole.Assistant && !hasText ? display : ExtractContent(text)),
                SearchableText = Searchable($"{RoleName(role)} {ExtractContent(text)}"),
                CopyText = copyText,
                HasText = hasText,
                HasErrorStop = isErrorOrAborted,
                IsBookkeeping = false,
                AssistantPlaceholder = placeholder
            };
        }
        return new TerminalTreeEntryInfo
        {
            DisplayKind = "message",
            Prefix = $"[{RoleName(role)}]: ",
            Text = Normalize(ExtractContent(text)),
            SearchableText = Searchable($"{RoleName(role)} {ExtractContent(text)}"),
            IsBookkeeping = false
        };
    }

    private TerminalTreeEntryInfo DecodeBashExecution(ConversationNode node)
    {
        var execution = ConversationMessageCodec.RestoreBashExecution(node.Payload, node.Id);
        var command = execution.Command ?? "";
        return new TerminalTreeEntryInfo
        {
            DisplayKind = "bashExecution",
            Prefix = "[bash]: ",
            Text = Normalize(command),
            SearchableText = Searchable($"bashExecution {command}"),
            CopyText = command.Length > 0 ? command : null,
            HasText = command.Trim().Length > 0,
            IsBookkeeping = false
        };
    }

    private TerminalTreeEntryInfo DecodeCustomMessage(ConversationNode node)
    {
        var text = ExtractCustomMessageContent(node.Payload, out var customType);
        var display = $"[{TerminalTreeText.Line(customType ?? "custom")}]: ";
        var copyText = text?.Length > 0 ? text : null;
        return new TerminalTreeEntryInfo
        {
            DisplayKind = "custom_message",
            Prefix = display,
            Text = Normalize(ExtractContent(text ?? "")),
            SearchableText = Searchable($"{customType ?? "custom"} {text ?? ""}"),
            CopyText = copyText,
            HasText = (text ?? "").Trim().Length > 0,
            IsBookkeeping = false
        };
    }

    private TerminalTreeEntryInfo DecodeCompaction(ConversationNode node)
    {
        var tokensBefore = 0L;
        _ = node.Payload.TryGetProperty("tokensBefore", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out tokensBefore);
        var summary = PayloadString(node.Payload, "summary");
        return new TerminalTreeEntryInfo
        {
            DisplayKind = "compaction",
            Prefix = $"[compaction: {(long)Math.Round(tokensBefore / 1000d)}k tokens]",
            Text = "",
            SearchableText = "compaction",
            CopyText = string.IsNullOrWhiteSpace(summary) ? null : summary,
            IsBookkeeping = false
        };
    }

    private TerminalTreeEntryInfo DecodeBranchSummary(ConversationNode node)
    {
        var summary = PayloadString(node.Payload, "summary") ?? "";
        return new TerminalTreeEntryInfo
        {
            DisplayKind = "branch_summary",
            Prefix = "[branch summary]: ",
            Text = Normalize(summary),
            SearchableText = Searchable($"branch summary {summary}"),
            CopyText = string.IsNullOrWhiteSpace(summary) ? null : summary,
            HasText = summary.Trim().Length > 0,
            IsBookkeeping = false
        };
    }

    private TerminalTreeEntryInfo DecodePrefix(ConversationNode node, string displayKind, string prefix,
        string? value, string searchable, bool isSettingsEntry) =>
        new()
        {
            DisplayKind = displayKind,
            Prefix = prefix,
            Text = Normalize(value ?? ""),
            SearchableText = Searchable(searchable),
            IsSettingsEntry = isSettingsEntry,
            IsBookkeeping = false
        };

    private static JsonElement? PiEntry(ConversationNode node) =>
        PiJsonlEntryCodec.OriginalEntry(node) is { } entry && entry.ValueKind == JsonValueKind.Object
            ? entry : null;

    private static string? PayloadString(JsonElement payload, string name) =>
        TryString(payload, name);

    private static bool ReplacementIsNull(JsonElement payload) =>
        payload.TryGetProperty("replacement", out var value) && value.ValueKind == JsonValueKind.Null;

    private static string Searchable(string value) => value.ToLowerInvariant();

    private static string Normalize(string value) => TerminalTreeText.Line(value);

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];

    private static string ExtractContent(string text) => Clip(text, 200);

    private static string ExtractFullContent(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));

    private static string? ExtractCustomMessageContent(JsonElement payload, out string? customType)
    {
        customType = PayloadString(payload, "customType");
        if (!payload.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var builder = new StringBuilder();
        foreach (var part in content.EnumerateArray())
            if (part.ValueKind == JsonValueKind.Object &&
                StringProperty(part, "type") == "text" && StringProperty(part, "text") is { } text)
                builder.Append(text);
        return builder.Length > 0 ? builder.ToString() : null;
    }

    private static string? Additional(ChatMessage message, string key) =>
        message.AdditionalProperties is { } properties &&
        properties.TryGetValue(key, out var value) && value is string text && text.Length > 0
            ? text
            : null;

    private static string RoleName(ChatRole role) => role == ChatRole.User ? "user"
        : role == ChatRole.Assistant ? "assistant"
        : role == ChatRole.Tool ? "toolResult"
        : role == ChatRole.System ? "system"
        : role.ToString().ToLowerInvariant();

    private static string? TryString(JsonElement? payload, string name) =>
        payload.HasValue && TryPropertyName(payload.Value, name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryPropertyName(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }
        foreach (var property in payload.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }

    private static string? StringProperty(JsonElement payload, string name) => TryString(payload, name);

    private static string FormatToolCall(string name, IDictionary<string, object?> arguments)
    {
        static string? StringArgument(IDictionary<string, object?> arguments, string name)
        {
            if (!arguments.TryGetValue(name, out var value) || value is null) return null;
            return TerminalTreeText.Line(value as string ?? value.ToString() ?? "");
        }

        static string ShortenPath(string path)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal))
                return "~" + path[home.Length..];
            return path;
        }

        switch (name)
        {
            case "read":
                return $"[read: {ShortenPath(StringArgument(arguments, "path") ?? StringArgument(arguments, "file_path") ?? "")}]";
            case "write":
                return $"[write: {ShortenPath(StringArgument(arguments, "path") ?? StringArgument(arguments, "file_path") ?? "")}]";
            case "edit":
                return $"[edit: {ShortenPath(StringArgument(arguments, "path") ?? StringArgument(arguments, "file_path") ?? "")}]";
            case "bash":
                {
                    var raw = StringArgument(arguments, "command") ?? "";
                    var normalized = Normalize(raw);
                    const int maximum = 50;
                    var clipped = normalized.Length <= maximum ? normalized : normalized[..maximum];
                    return $"[bash: {clipped}{(normalized.Length > maximum ? "..." : "")}]";
                }
            case "grep":
                return $"[grep: /{StringArgument(arguments, "pattern") ?? ""}/ in {ShortenPath(StringArgument(arguments, "path") ?? ".")}]";
            case "find":
                return $"[find: {StringArgument(arguments, "pattern") ?? ""} in {ShortenPath(StringArgument(arguments, "path") ?? ".")}]";
            case "ls":
                return $"[ls: {ShortenPath(StringArgument(arguments, "path") ?? ".")}]";
            default:
                {
                    var argsText = TerminalTreeText.Line(JsonSerializer.Serialize(arguments));
                    var safeName = TerminalTreeText.Line(name);
                    const int maximum = 40;
                    return $"[{safeName}: {(argsText.Length <= maximum ? argsText : argsText[..maximum])}{(argsText.Length > maximum ? "..." : "")}]";
                }
        }
    }
}
