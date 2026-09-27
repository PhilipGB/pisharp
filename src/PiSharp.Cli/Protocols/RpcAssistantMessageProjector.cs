using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcAssistantMessageProjector
{
    private readonly List<JsonObject> _content = [];
    private readonly List<JsonObject> _runMessages = [];
    private readonly Dictionary<string, JsonObject> _toolResults = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ActiveToolCall> _streamedToolCalls = [];
    private readonly Dictionary<string, ActiveToolCall> _streamedToolCallsById = new(StringComparer.Ordinal);
    private readonly StringBuilder _activeContent = new();
    private string? _activeKind;
    private int _activeContentIndex = -1;
    private bool _messageStarted;
    private bool _providerRequestActive;
    private DateTimeOffset _timestamp;
    private UsageRecord? _usage;

    public JsonObject? LastCompletedAssistantMessage { get; private set; }

    public JsonObject? ActiveProviderMessage(ConversationSession conversation, string? api) =>
        _providerRequestActive ? CreatePartialMessage(conversation, api) : null;

    public void BeginAgent()
    {
        _runMessages.Clear();
        BeginTurn();
    }

    public void BeginTurn()
    {
        ResetProviderMessage();
        LastCompletedAssistantMessage = null;
        _toolResults.Clear();
    }

    public void BeginProviderMessage()
    {
        ResetProviderMessage();
        _providerRequestActive = true;
    }

    public IReadOnlyList<object> ProjectPrompt(AgentLifecycleEvent item, ConversationSession conversation)
    {
        if (item.PromptMessage is not { } prompt) return [];
        var timestamp = item.MessageTimestamp ?? DateTimeOffset.UtcNow;
        var message = PiJsonlSessionInterchange.ProjectRuntimeMessage(conversation, prompt, timestamp: timestamp);
        _runMessages.Add(message.DeepClone().AsObject());
        return [new JsonObject { ["type"] = "message_start", ["message"] = message.DeepClone() },
            new JsonObject { ["type"] = "message_end", ["message"] = message }];
    }

    public IReadOnlyList<object> ProjectUpdate(AgentLifecycleEvent item, ConversationSession conversation, string? api)
    {
        if (item.UsageSnapshot is { } usage) _usage = usage;
        var records = new List<object>();
        if (item.StreamedToolCallDelta is { } toolCallDelta)
            ProjectToolCallDelta(toolCallDelta, records, conversation, api);
        if (item.ProviderUpdate is not { } update) return records;

        // A pre-content provider retry must not leave an unmatched message_start on the wire.
        var sawText = false;
        foreach (var content in update.Contents ?? [])
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    AppendBlock("text", text.Text, records, conversation, api);
                    sawText = true;
                    break;
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    AppendBlock("thinking", reasoning.Text, records, conversation, api);
                    break;
                case FunctionCallContent:
                    EnsureStarted(records, conversation, api);
                    break;
                case UsageContent:
                    break;
                default:
                    EnsureStarted(records, conversation, api);
                    break;
            }
        }

        if (!sawText && update.Contents?.OfType<TextContent>().Any() != true && !string.IsNullOrEmpty(update.Text))
            AppendBlock("text", update.Text, records, conversation, api);
        return records;
    }

    public IReadOnlyList<object> CompleteProviderMessage(AgentLifecycleEvent item,
        ConversationSession conversation, string? api)
    {
        if (item.UsageSnapshot is { } usage) _usage = usage;
        var response = item.ProviderResponse;
        if (response is null) return [];

        var records = new List<object>();
        EnsureStarted(records, conversation, api);
        FinishActiveContent(records, conversation, api);
        var contentIndex = _content.Count;
        foreach (var call in response.Contents.OfType<FunctionCallContent>())
        {
            if (_streamedToolCallsById.TryGetValue(call.CallId, out var streamed))
                CompleteStreamedToolCall(call, streamed, records);
            else ProjectToolCall(call, contentIndex++, records, conversation, api);
        }

        var message = PiJsonlSessionInterchange.ProjectRuntimeMessage(conversation, response, api,
            timestamp: _timestamp);
        message["stopReason"] = ProjectStopReason(item.ProviderFinishReason, response);
        if (!string.IsNullOrWhiteSpace(item.ProviderModelId) && item.ProviderModelId != conversation.Model)
            message["responseModel"] = item.ProviderModelId;
        if (!string.IsNullOrWhiteSpace(item.ProviderResponseId)) message["responseId"] = item.ProviderResponseId;
        if (!string.IsNullOrWhiteSpace(item.ProviderThinkingLevel)) message["providerThinkingLevel"] = item.ProviderThinkingLevel;
        message["usage"] = ProjectUsage(_usage);
        records.Add(new JsonObject { ["type"] = "message_end", ["message"] = message.DeepClone() });
        LastCompletedAssistantMessage = message.DeepClone().AsObject();
        _runMessages.Add(message.DeepClone().AsObject());
        ResetProviderMessage();
        return records;
    }

    public IReadOnlyList<object> FailProviderMessage(AgentLifecycleEvent item,
        ConversationSession conversation, string? api)
    {
        if (!_providerRequestActive)
        {
            ResetProviderMessage();
            return [];
        }
        var records = new List<object>();
        EnsureStarted(records, conversation, api);
        FinishActiveContent(records, conversation, api);
        var message = CreatePartialMessage(conversation, api);
        var interrupted = item.Type is "model_request_interrupted" or "turn_interrupted";
        message["stopReason"] = interrupted ? "aborted" : "error";
        if (interrupted)
            message["errorMessage"] = string.IsNullOrWhiteSpace(item.Error) ? "Request was aborted" : item.Error;
        else if (!string.IsNullOrWhiteSpace(item.Error))
            message["errorMessage"] = item.Error;
        records.Add(new JsonObject { ["type"] = "message_end", ["message"] = message.DeepClone() });
        LastCompletedAssistantMessage = message.DeepClone().AsObject();
        _runMessages.Add(message.DeepClone().AsObject());
        ResetProviderMessage();
        return records;
    }

    public IReadOnlyList<object> ProjectToolResult(AgentLifecycleEvent item, ConversationSession conversation)
    {
        if (item.ToolResultMessage is not { } result) return [];
        var timestamp = DateTimeOffset.UtcNow;
        var message = PiJsonlSessionInterchange.ProjectRuntimeMessage(conversation, result,
            toolName: item.Tool, timestamp: timestamp);
        _runMessages.Add(message.DeepClone().AsObject());
        var callId = result.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId;
        if (callId is not null) _toolResults[callId] = message.DeepClone().AsObject();
        return [new JsonObject { ["type"] = "message_start", ["message"] = message.DeepClone() },
            new JsonObject { ["type"] = "message_end", ["message"] = message }];
    }

    public JsonObject? GetToolResult(string callId) =>
        _toolResults.TryGetValue(callId, out var message) ? message.DeepClone().AsObject() : null;

    public JsonArray ReconcileRunMessages(JsonArray canonicalMessages, bool includeUnpersisted = false)
    {
        if (_runMessages.Count == 0) return canonicalMessages.DeepClone().AsArray();
        var result = new JsonArray();
        var snapshotIndex = 0;
        foreach (var canonical in canonicalMessages.OfType<JsonObject>())
        {
            var projected = canonical;
            if (snapshotIndex < _runMessages.Count && SameMessage(canonical, _runMessages[snapshotIndex]))
                projected = _runMessages[snapshotIndex++];
            result.Add(projected.DeepClone());
        }
        if (includeUnpersisted)
            for (; snapshotIndex < _runMessages.Count; snapshotIndex++)
                result.Add(_runMessages[snapshotIndex].DeepClone());
        return result;
    }

    private void AppendBlock(string kind, string text, List<object> records,
        ConversationSession conversation, string? api)
    {
        EnsureStarted(records, conversation, api);
        if (_activeKind != kind)
        {
            FinishActiveContent(records, conversation, api);
            _activeKind = kind;
            _activeContentIndex = _content.Count;
            _activeContent.Clear();
            var type = kind == "text" ? "text" : "thinking";
            var property = kind == "text" ? "text" : "thinking";
            _content.Add(new JsonObject { ["type"] = type, [property] = "" });
            records.Add(MessageUpdate(new JsonObject
            {
                ["type"] = kind + "_start",
                ["contentIndex"] = _activeContentIndex
            }));
        }

        _activeContent.Append(text);
        _content[_activeContentIndex][kind == "text" ? "text" : "thinking"] = _activeContent.ToString();
        records.Add(MessageUpdate(new JsonObject
        {
            ["type"] = kind + "_delta",
            ["contentIndex"] = _activeContentIndex,
            ["delta"] = text
        }));
    }

    private void FinishActiveContent(List<object> records, ConversationSession conversation, string? api)
    {
        if (_activeKind is null) return;
        var kind = _activeKind;
        var property = kind == "text" ? "text" : "thinking";
        var complete = _activeContent.ToString();
        _content[_activeContentIndex][property] = complete;
        records.Add(MessageUpdate(new JsonObject
        {
            ["type"] = kind + "_end",
            ["contentIndex"] = _activeContentIndex,
            ["content"] = complete
        }));
        _activeKind = null;
        _activeContentIndex = -1;
        _activeContent.Clear();
    }

    private void ProjectToolCall(FunctionCallContent call, int contentIndex, List<object> records,
        ConversationSession conversation, string? api)
    {
        var arguments = JsonSerializer.SerializeToNode(call.Arguments) ?? new JsonObject();
        _content.Add(new JsonObject
        {
            ["type"] = "toolCall",
            ["id"] = call.CallId,
            ["name"] = call.Name,
            ["arguments"] = arguments.DeepClone()
        });
        records.Add(MessageUpdate(new JsonObject
        {
            ["type"] = "toolcall_start",
            ["contentIndex"] = contentIndex,
            ["id"] = call.CallId,
            ["toolName"] = call.Name
        }));
        records.Add(MessageUpdate(new JsonObject
        {
            ["type"] = "toolcall_delta",
            ["contentIndex"] = contentIndex,
            ["delta"] = JsonSerializer.Serialize(call.Arguments)
        }));
        records.Add(MessageUpdate(new JsonObject
        {
            ["type"] = "toolcall_end",
            ["contentIndex"] = contentIndex,
            ["toolCall"] = new JsonObject
            {
                ["type"] = "toolCall",
                ["id"] = call.CallId,
                ["name"] = call.Name,
                ["arguments"] = arguments.DeepClone()
            }
        }));
    }

    private void ProjectToolCallDelta(ProviderToolCallDelta delta, List<object> records,
        ConversationSession conversation, string? api)
    {
        EnsureStarted(records, conversation, api);
        FinishActiveContent(records, conversation, api);
        if (!_streamedToolCalls.TryGetValue(delta.Index, out var call))
        {
            call = new ActiveToolCall();
            _streamedToolCalls.Add(delta.Index, call);
        }
        call.CallId = delta.CallId ?? call.CallId;
        call.Name = delta.Name ?? call.Name;
        if (delta.CallId is not null) _streamedToolCallsById[delta.CallId] = call;
        if (!string.IsNullOrEmpty(delta.Arguments)) call.ArgumentFragments.Add(delta.Arguments);
        if (!call.Started && call.CallId is not null && call.Name is not null)
        {
            call.ContentIndex = _content.Count;
            call.Started = true;
            _content.Add(new JsonObject
            {
                ["type"] = "toolCall",
                ["id"] = call.CallId,
                ["name"] = call.Name,
                ["arguments"] = new JsonObject()
            });
            records.Add(MessageUpdate(new JsonObject
            {
                ["type"] = "toolcall_start",
                ["contentIndex"] = call.ContentIndex,
                ["id"] = call.CallId,
                ["toolName"] = call.Name
            }));
            foreach (var fragment in call.ArgumentFragments)
                records.Add(ToolCallDeltaUpdate(call.ContentIndex, fragment));
        }
        else if (call.Started && !string.IsNullOrEmpty(delta.Arguments))
            records.Add(ToolCallDeltaUpdate(call.ContentIndex, delta.Arguments));
        if (call.Started) RefreshPartialToolArguments(call);
    }

    private void CompleteStreamedToolCall(FunctionCallContent completed, ActiveToolCall streamed, List<object> records)
    {
        var toolCall = new JsonObject
        {
            ["type"] = "toolCall",
            ["id"] = completed.CallId,
            ["name"] = completed.Name,
            ["arguments"] = JsonSerializer.SerializeToNode(completed.Arguments) ?? new JsonObject()
        };
        if (!streamed.Started)
        {
            streamed.CallId = completed.CallId;
            streamed.Name = completed.Name;
            streamed.ContentIndex = _content.Count;
            streamed.Started = true;
            _content.Add(toolCall.DeepClone().AsObject());
            records.Add(MessageUpdate(new JsonObject
            {
                ["type"] = "toolcall_start",
                ["contentIndex"] = streamed.ContentIndex,
                ["id"] = completed.CallId,
                ["toolName"] = completed.Name
            }));
            foreach (var fragment in streamed.ArgumentFragments)
                records.Add(ToolCallDeltaUpdate(streamed.ContentIndex, fragment));
        }
        else _content[streamed.ContentIndex] = toolCall.DeepClone().AsObject();
        records.Add(MessageUpdate(new JsonObject
        {
            ["type"] = "toolcall_end",
            ["contentIndex"] = streamed.ContentIndex,
            ["toolCall"] = toolCall
        }));
    }

    private void RefreshPartialToolArguments(ActiveToolCall call)
    {
        try
        {
            if (JsonNode.Parse(string.Concat(call.ArgumentFragments)) is JsonObject arguments)
                _content[call.ContentIndex]["arguments"] = arguments;
        }
        catch (JsonException) { }
    }

    private JsonObject ToolCallDeltaUpdate(int contentIndex, string delta) => MessageUpdate(new JsonObject
    {
        ["type"] = "toolcall_delta",
        ["contentIndex"] = contentIndex,
        ["delta"] = delta
    });

    private sealed class ActiveToolCall
    {
        public int ContentIndex { get; set; } = -1;
        public string? CallId { get; set; }
        public string? Name { get; set; }
        public bool Started { get; set; }
        public List<string> ArgumentFragments { get; } = [];
    }

    private void EnsureStarted(List<object> records, ConversationSession conversation, string? api)
    {
        if (_messageStarted) return;
        _messageStarted = true;
        _timestamp = DateTimeOffset.UtcNow;
        records.Add(new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = CreatePartialMessage(conversation, api)
        });
    }

    private JsonObject CreatePartialMessage(ConversationSession conversation, string? api) => new()
    {
        ["role"] = "assistant",
        ["content"] = new JsonArray(_content.Select(content => (JsonNode?)content.DeepClone()).ToArray()),
        ["api"] = string.IsNullOrWhiteSpace(api) ? "openai-responses" : api,
        ["provider"] = conversation.Provider,
        ["model"] = conversation.Model,
        ["stopReason"] = "pending",
        ["timestamp"] = (_timestamp == default ? DateTimeOffset.UtcNow : _timestamp).ToUnixTimeMilliseconds(),
        ["usage"] = ProjectUsage(_usage)
    };

    private JsonObject MessageUpdate(JsonObject assistantMessageEvent) => new()
    {
        ["type"] = "message_update",
        ["usage"] = ProjectUsage(_usage),
        ["assistantMessageEvent"] = assistantMessageEvent
    };

    private static JsonObject ProjectUsage(UsageRecord? usage)
    {
        var inputCost = usage?.InputCost ?? 0;
        var outputCost = usage?.OutputCost ?? 0;
        var cachedInputCost = usage?.CachedInputCost ?? 0;
        var cachedWriteCost = usage?.CachedWriteCost ?? 0;
        var result = new JsonObject
        {
            ["input"] = usage?.InputTokens ?? 0,
            ["output"] = usage?.OutputTokens ?? 0,
            ["cacheRead"] = usage?.CachedInputTokens ?? 0,
            ["cacheWrite"] = usage?.CachedWriteTokens ?? 0,
            ["totalTokens"] = usage?.TotalTokens ?? 0,
            ["cost"] = new JsonObject
            {
                ["input"] = inputCost,
                ["output"] = outputCost,
                ["cacheRead"] = cachedInputCost,
                ["cacheWrite"] = cachedWriteCost,
                ["total"] = usage?.Cost ?? 0
            }
        };
        if (usage?.ReasoningTokens > 0) result["reasoning"] = usage.ReasoningTokens;
        return result;
    }

    private static string ProjectStopReason(string? finishReason, ChatMessage response)
    {
        if (response.Contents.OfType<FunctionCallContent>().Any()) return "toolUse";
        return finishReason?.ToLowerInvariant() switch
        {
            "length" => "length",
            "content_filter" or "contentfilter" or "error" => "error",
            _ => "stop"
        };
    }

    private static bool SameMessage(JsonObject left, JsonObject right) =>
        left["role"]?.GetValue<string>() == right["role"]?.GetValue<string>() &&
        JsonNode.DeepEquals(left["content"], right["content"]);

    private void ResetProviderMessage()
    {
        _content.Clear();
        _streamedToolCalls.Clear();
        _streamedToolCallsById.Clear();
        _activeKind = null;
        _activeContentIndex = -1;
        _activeContent.Clear();
        _messageStarted = false;
        _providerRequestActive = false;
        _timestamp = default;
        _usage = null;
    }
}
