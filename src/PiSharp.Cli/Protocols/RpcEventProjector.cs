using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcEventProjector
{
    private readonly RpcAssistantMessageProjector _assistantMessages = new();

    public void BeginAgent() => _assistantMessages.BeginAgent();

    public void BeginTurn() => _assistantMessages.BeginTurn();

    public IReadOnlyList<object> ProjectPrompt(AgentLifecycleEvent item, ConversationSession conversation) =>
        _assistantMessages.ProjectPrompt(item, conversation);

    public IReadOnlyList<object> Project(AgentLifecycleEvent item, ConversationSession conversation,
        string? turnStartHead, string? runStartHead, bool runAccepted, string? api, bool turnOpen = true)
    {
        if (item.Type == "assistant_turn_started")
        {
            _assistantMessages.BeginTurn();
            return [new { type = "turn_start" }];
        }

        if (item.Type == "model_request_started")
        {
            _assistantMessages.BeginProviderMessage();
            return [];
        }

        if (item.Type is "model_text_delta" or "model_content_update")
            return _assistantMessages.ProjectUpdate(item, conversation, api);

        if (item.Type == "model_request_completed")
            return _assistantMessages.CompleteProviderMessage(item, conversation, api);

        if (item.Type == "tool_execution_finished")
        {
            var projected = new List<object>(3) { ProjectToolEnd(item) };
            projected.AddRange(_assistantMessages.ProjectToolResult(item, conversation));
            return projected;
        }

        if (item.Type == "assistant_turn_completed" && runAccepted)
            return [ProjectCompletedTurn(item, conversation, api)];

        if ((item.Type is "turn_failed" or "turn_interrupted") && runAccepted)
        {
            var terminalType = item.Type == "turn_interrupted" ? item.Type : "turn_failed";
            var projected = new List<object>(_assistantMessages.FailProviderMessage(item, conversation, api));
            if (turnOpen && ProjectTurnEnd(conversation, turnStartHead, api, terminalType, item.Error,
                    item.TurnEndHead, _assistantMessages.LastCompletedAssistantMessage,
                    item.TurnMessage, item.TurnToolResults) is { } turnEnd)
                projected.Add(turnEnd);
            projected.Add(ProjectAgentEnd(conversation, runStartHead, api, terminalType, item.Error,
                item.TurnEndHead, item.WillRetry ?? false));
            return projected;
        }

        if (item.Type == "agent_run_completed" && runAccepted)
            return [ProjectAgentEnd(conversation, runStartHead, api, null, null, conversation.Tree.HeadId, false)];

        if (item.Type == "tool_execution_started") return [ProjectToolStart(item)];
        if (item.Type == "tool_execution_update") return [ProjectToolUpdate(item)];

        var record = ProjectOther(item, conversation, runAccepted, api);
        return record is null ? [] : [record];
    }

    private static object? ProjectOther(AgentLifecycleEvent item, ConversationSession conversation,
        bool runAccepted, string? api) => item.Type switch
        {
            "prompt_queued" or "prompt_accepted" or "agent_attempt_started" or "prompt_rejected" or "agent_run_completed" or "agent_settled" or
                "steering_message_accepted" or "turn_completed" or "turn_failed" or "turn_interrupted" or "assistant_turn_started" or
                "assistant_turn_completed" or "model_request_started" or "model_request_completed" or
                "model_request_failed" or "model_request_interrupted" or "model_text_delta" or "model_content_update" => null,
            "queue_update" => ProjectQueueUpdate(item),
            "entry_appended" => ProjectAppendedEntry(item),
            "auto_retry_start" => new
            {
                type = "auto_retry_start",
                attempt = item.RetryAttempt,
                maxAttempts = item.RetryMaxAttempts,
                delayMs = item.RetryDelayMs,
                errorMessage = item.Error
            },
            "auto_retry_end" => ProjectRetryEnd(item),
            _ => new { type = "event", format = "pisharp", data = item }
        };

    private static object ProjectAppendedEntry(AgentLifecycleEvent item) => new
    {
        type = "entry_appended",
        entry = item.AppendedEntry ?? throw new InvalidOperationException("Appended-entry event has no session entry.")
    };

    private object ProjectCompletedTurn(AgentLifecycleEvent item, ConversationSession conversation, string? api)
    {
        var message = item.TurnMessage ?? throw new InvalidOperationException("Completed provider turn has no assistant message.");
        var toolNames = message.Contents.OfType<FunctionCallContent>()
            .ToDictionary(call => call.CallId, call => call.Name, StringComparer.Ordinal);
        var results = item.TurnToolResults?.ToArray() ?? [];
        var ordered = OrderToolResults(message, results);
        var toolResults = new JsonArray();
        foreach (var result in ordered)
        {
            var callId = result.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId;
            var projectedResult = callId is null ? null : _assistantMessages.GetToolResult(callId);
            toolResults.Add(projectedResult ?? PiJsonlSessionInterchange.ProjectRuntimeMessage(conversation, result,
                toolName: callId is not null && toolNames.TryGetValue(callId, out var name) ? name : "tool"));
        }

        return new JsonObject
        {
            ["type"] = "turn_end",
            ["message"] = _assistantMessages.LastCompletedAssistantMessage?.DeepClone() ??
                PiJsonlSessionInterchange.ProjectRuntimeMessage(conversation, message, api),
            ["toolResults"] = toolResults
        };
    }

    private static IReadOnlyList<ChatMessage> OrderToolResults(ChatMessage message, IReadOnlyList<ChatMessage> results)
    {
        var byCallId = results.SelectMany(result => result.Contents.OfType<FunctionResultContent>()
                .Select(content => (content.CallId, Result: result)))
            .GroupBy(item => item.CallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Result, StringComparer.Ordinal);
        var ordered = message.Contents.OfType<FunctionCallContent>()
            .Where(call => byCallId.ContainsKey(call.CallId))
            .Select(call => byCallId[call.CallId]).ToList();
        ordered.AddRange(results.Where(result => !ordered.Contains(result)));
        return ordered;
    }

    private static object ProjectToolStart(AgentLifecycleEvent item) => new
    {
        type = "tool_execution_start",
        toolCallId = item.ToolCallId ?? item.OperationId ?? "unknown-call",
        toolName = item.Tool,
        args = JsonSerializer.SerializeToNode(item.ToolArguments) ?? new JsonObject()
    };

    private static object ProjectToolUpdate(AgentLifecycleEvent item) => new
    {
        type = "tool_execution_update",
        toolCallId = item.ToolCallId ?? item.OperationId ?? "unknown-call",
        toolName = item.Tool,
        args = JsonSerializer.SerializeToNode(item.ToolArguments) ?? new JsonObject(),
        partialResult = new
        {
            content = new[] { new { type = "text", text = item.Text ?? "" } },
            details = JsonSerializer.SerializeToNode(item.Details) ?? new JsonObject()
        }
    };

    private static object ProjectToolEnd(AgentLifecycleEvent item) => new
    {
        type = "tool_execution_end",
        toolCallId = item.ToolCallId ?? item.OperationId ?? "unknown-call",
        toolName = item.Tool,
        result = new
        {
            content = new[] { new { type = "text", text = item.Text ?? "" } },
            details = JsonSerializer.SerializeToNode(item.Details) ?? new JsonObject()
        },
        isError = item.IsError ?? false
    };

    private object? ProjectTurnEnd(ConversationSession conversation, string? turnStartHead, string? api,
        string? terminalType, string? errorMessage, string? turnEndHead,
        JsonObject? completedAssistantMessage, ChatMessage? turnMessage, IReadOnlyList<ChatMessage>? turnToolResults)
    {
        var messages = PiJsonlSessionInterchange.ProjectRunMessages(conversation, turnStartHead, api,
                terminalType, errorMessage, turnEndHead)
            .OfType<JsonObject>().ToArray();
        var assistantIndex = Array.FindLastIndex(messages, message =>
            message["role"]?.GetValue<string>() == "assistant");
        if (assistantIndex < 0 && completedAssistantMessage is null) return null;

        var toolResults = new JsonArray();
        var results = turnToolResults ?? [];
        var toolNames = turnMessage?.Contents.OfType<FunctionCallContent>()
            .ToDictionary(call => call.CallId, call => call.Name, StringComparer.Ordinal) ??
            new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            var callId = result.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId;
            var projected = callId is null ? null : _assistantMessages.GetToolResult(callId);
            toolResults.Add(projected ?? PiJsonlSessionInterchange.ProjectRuntimeMessage(conversation, result,
                toolName: callId is not null && toolNames.TryGetValue(callId, out var name) ? name : "tool"));
        }
        if (completedAssistantMessage is null && results.Count == 0)
            foreach (var message in messages.Skip(assistantIndex + 1)
                         .Where(message => message["role"]?.GetValue<string>() == "toolResult"))
                toolResults.Add(message.DeepClone());

        return new JsonObject
        {
            ["type"] = "turn_end",
            ["message"] = completedAssistantMessage?.DeepClone() ?? messages[assistantIndex].DeepClone(),
            ["toolResults"] = toolResults
        };
    }

    private object ProjectAgentEnd(ConversationSession conversation, string? runStartHead, string? api,
        string? terminalType, string? errorMessage, string? runEndHead, bool willRetry)
    {
        var canonicalMessages = PiJsonlSessionInterchange.ProjectRunMessages(conversation, runStartHead, api,
            terminalType, errorMessage, runEndHead);
        return new JsonObject
        {
            ["type"] = "agent_end",
            ["messages"] = _assistantMessages.ReconcileRunMessages(canonicalMessages),
            ["willRetry"] = willRetry
        };
    }

    private static object ProjectQueueUpdate(AgentLifecycleEvent item)
    {
        using var payload = JsonDocument.Parse(item.Text ?? "{}");
        return new
        {
            type = "queue_update",
            steering = ReadQueue(payload.RootElement, "steering"),
            followUp = ReadQueue(payload.RootElement, "followUp")
        };
    }

    private static JsonObject ProjectRetryEnd(AgentLifecycleEvent item)
    {
        var record = new JsonObject
        {
            ["type"] = "auto_retry_end",
            ["success"] = item.RetrySuccess ?? false,
            ["attempt"] = item.RetryAttempt ?? 0
        };
        if (item.RetryFinalError is not null) record["finalError"] = item.RetryFinalError;
        return record;
    }

    private static string[] ReadQueue(JsonElement payload, string propertyName) =>
        payload.TryGetProperty(propertyName, out var queue) && queue.ValueKind == JsonValueKind.Array
            ? queue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToArray()
            : [];
}
