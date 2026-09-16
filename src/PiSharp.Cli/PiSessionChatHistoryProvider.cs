using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

/// <summary>
/// Rehydrates MAF requests from Pi's typed message chain. The provider is deliberately
/// read-only: PiSharp owns durable writes and MAF serialization remains only a cache.
/// </summary>
internal sealed class PiSessionChatHistoryProvider : ChatHistoryProvider
{
    private readonly object _sync = new();
    private readonly Dictionary<AgentSession, List<ChatMessage>> _ephemeralHistory = [];
    private SessionDocument? _document;
    private string? _activeEntryId;

    public void SetActiveDocument(SessionDocument? document, string? activeEntryId)
    {
        lock (_sync)
        {
            _document = document;
            _activeEntryId = activeEntryId;
        }
    }

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionDocument? document;
        string? activeEntryId;
        lock (_sync)
        {
            document = _document;
            activeEntryId = _activeEntryId;
        }

        var history = document is null
            ? context.Session is null ? [] : GetEphemeralHistory(context.Session)
            : document.IsPiV3
                ? document.GetActiveEntryMessages(activeEntryId)
                : document.GetLegacyMessages(activeEntryId);
        var request = context.RequestMessages.ToArray();
        if (request.Length > 0 && history.Count > 0 && SameMessage(history[^1], request[^1]))
        {
            history = history.Take(history.Count - 1).ToArray();
        }
        return ValueTask.FromResult<IEnumerable<ChatMessage>>(history);
    }

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if ((_document is null || !_document.IsPiV3) && context.InvokeException is null && context.Session is not null)
            {
                var history = _ephemeralHistory.TryGetValue(context.Session, out var existing)
                    ? existing
                    : [];
                history.AddRange(context.RequestMessages ?? []);
                history.AddRange(context.ResponseMessages ?? []);
                _ephemeralHistory[context.Session] = history;
            }
        }
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<ChatMessage> GetEphemeralHistory(AgentSession session)
    {
        lock (_sync)
        {
            return _ephemeralHistory.TryGetValue(session, out var history)
                ? history.ToArray()
                : [];
        }
    }

    private static bool SameMessage(ChatMessage left, ChatMessage right) =>
        left.Role == right.Role &&
        string.Equals(GetText(left), GetText(right), StringComparison.Ordinal);

    private static string GetText(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));
}

internal static class PiSessionDocumentMessages
{
    public static IReadOnlyList<ChatMessage> GetLegacyMessages(
        this SessionDocument document,
        string? activeEntryId) =>
        document.GetActivePath(activeEntryId)
            .SelectMany(turn => new ChatMessage[]
            {
                new(ChatRole.User, turn.UserMessage),
                new(ChatRole.Assistant, turn.AssistantMessage),
            })
            .ToArray();

    public static IReadOnlyList<ChatMessage> GetActiveEntryMessages(
        this SessionDocument document,
        string? activeEntryId)
    {
        var path = document.GetActiveEntryPath(activeEntryId);
        return PiCompactionPlanner.BuildContextEntries(path)
            .Select(ToChatMessage)
            .Where(message => message is not null)
            .Cast<ChatMessage>()
            .ToArray();
    }

    private static ChatMessage? ToChatMessage(SessionEntry entry) => entry switch
    {
        MessageEntry message => ToMessage(message.Message),
        BashExecutionEntry { ExcludeFromContext: true } => null,
        BashExecutionEntry bash => new ChatMessage(ChatRole.User, BashToText(JsonSerializer.SerializeToElement(new
        {
            command = bash.Command,
            output = bash.Output,
            exitCode = bash.ExitCode,
        }))),
        CustomMessageEntry custom => new ChatMessage(ChatRole.User, ReadContent(custom.Content)),
        CompactionEntry compaction => new ChatMessage(ChatRole.User, $"The conversation history before this point was compacted into the following summary:\n\n<summary>\n{compaction.Summary}\n</summary>"),
        BranchSummaryEntry branch => new ChatMessage(ChatRole.User, $"The following is a summary of a branch that this conversation came back from:\n\n<summary>\n{branch.Summary}\n</summary>"),
        _ => null,
    };

    private static ChatMessage? ToMessage(JsonElement message)
    {
        if (!message.TryGetProperty("role", out var roleValue))
        {
            return null;
        }

        return roleValue.GetString() switch
        {
            "user" => new ChatMessage(ChatRole.User, ReadContent(message)),
            "assistant" => new ChatMessage(ChatRole.Assistant, ReadContent(message)),
            "toolResult" => ToToolResult(message),
            "bashExecution" => new ChatMessage(ChatRole.User, BashToText(message)),
            "custom" => new ChatMessage(ChatRole.User, ReadContent(message)),
            _ => null,
        };
    }

    private static string BashToText(JsonElement message)
    {
        var command = GetString(message, "command");
        var output = GetString(message, "output");
        var text = $"Ran `{command}`\n";
        text += string.IsNullOrEmpty(output) ? "(no output)" : $"```\n{output}\n```";
        if (message.TryGetProperty("cancelled", out var cancelled) && cancelled.GetBoolean())
        {
            text += "\n\n(command cancelled)";
        }
        else if (message.TryGetProperty("exitCode", out var exitCode) &&
                 exitCode.ValueKind == JsonValueKind.Number && exitCode.GetInt32() != 0)
        {
            text += $"\n\nCommand exited with code {exitCode.GetInt32()}";
        }
        return text;
    }

    private static ChatMessage ToToolResult(JsonElement message)
    {
        var callId = message.TryGetProperty("toolCallId", out var callIdValue)
            ? callIdValue.GetString() ?? string.Empty
            : string.Empty;
        var text = ReadContent(message).OfType<TextContent>().Select(content => content.Text);
        return new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, string.Concat(text))]);
    }

    private static IList<AIContent> ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return [];
        }
        if (content.ValueKind == JsonValueKind.String)
        {
            return [new TextContent(content.GetString() ?? string.Empty)];
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return [new TextContent(content.ToString())];
        }

        var result = new List<AIContent>();
        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeValue))
            {
                continue;
            }
            switch (typeValue.GetString())
            {
                case "text":
                    result.Add(new TextContent(GetString(item, "text")));
                    break;
                case "thinking":
                    result.Add(new TextReasoningContent(GetString(item, "thinking")));
                    break;
                case "toolCall":
                    result.Add(CreateFunctionCall(item));
                    break;
                case "image":
                    result.Add(CreateImageContent(item));
                    break;
            }
        }
        return result;
    }

    private static DataContent CreateImageContent(JsonElement item)
    {
        var mimeType = GetString(item, "mimeType");
        var data = GetString(item, "data");
        return new DataContent($"data:{mimeType};base64,{data}", mimeType);
    }

    private static FunctionCallContent CreateFunctionCall(JsonElement item)
    {
        var arguments = item.TryGetProperty("arguments", out var argumentsValue)
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsValue.GetRawText()) ?? []
            : new Dictionary<string, object?>();
        return new FunctionCallContent(
            GetString(item, "id"),
            GetString(item, "name"),
            arguments);
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;
}
