using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

internal static class AgentTurnRunner
{
    public static async Task<LiveTurnResult> RunAsync(
        AgentBootstrap bootstrap,
        SessionController sessions,
        LiveTurnCoordinator liveTurns,
        string prompt,
        string workspaceRoot,
        IChatOutput output,
        CancellationToken cancellationToken,
        IReadOnlyList<AIContent>? initialContents = null)
    {
        var expandInput = CreateInputExpander(bootstrap, bootstrap.ExtensionHost);
        Action<string>? notify = output is TerminalChatOutput ? Console.WriteLine : null;
        var context = new PiSharpExtensionContext(workspaceRoot, liveTurns.Queue, cancellationToken, notify);
        output.AgentStarted();
        var initial = true;
        using var deliveryRegistration = sessions.IsPersistent && sessions.Document?.IsPiV3 == true
            ? liveTurns.Queue.RegisterDeliveryHandler(
                (message, token) => sessions.PersistUserMessageAsync(message.Text, null, token))
            : null;
        await bootstrap.ExtensionHost.PublishAsync(PiSharpExtensionEvent.BeforeTurn, context);

        // The whole outer turn (initial prompt plus any drained follow-ups) is one session
        // operation: prompts submitted while it runs are rejected deterministically instead of
        // interleaving with the turn's persistence.
        sessions.EnterTurn();
        LiveTurnResult result;
        try
        {
            result = await liveTurns.RunAsync(
                prompt,
                async (message, token) =>
                {
                    var contents = initial ? initialContents : null;
                    initial = false;
                    var expandedMessage = expandInput(message);
                    if (sessions.IsPersistent && sessions.Document?.IsPiV3 == true)
                    {
                        // Pi order: compact the existing history BEFORE persisting the new
                        // prompt, so the compaction boundary lands in front of the prompt that
                        // triggered it (and is never summarized together with it).
                        await sessions.TryAutoCompactAsync(output, CompactionReason.Threshold, token);
                        await sessions.PersistUserMessageAsync(expandedMessage, contents, token);
                    }

                    // Provider-request-level compaction and overflow recovery happen inside
                    // CompactionChatClient, per model request — there is deliberately no
                    // whole-turn replay loop here: on overflow only the failed provider
                    // request is retried, never the prompt or already-executed tools.
                    var execution = await RunSingleAsync(
                        bootstrap.Agent,
                        sessions.Session,
                        expandedMessage,
                        output,
                        token,
                        contents,
                        bootstrap.RetryPolicy,
                        sessions.Document?.IsPiV3 == true
                            ? messageSnapshot => sessions.PersistAssistantMessagesAsync([messageSnapshot], token)
                            : null);
                    if (sessions.IsPersistent && sessions.Document?.IsPiV3 == true && !execution.Cancelled)
                    {
                        // Post-run check: the response's provider usage may have exceeded the
                        // window even when the char-based estimate was below the threshold.
                        await sessions.TryAutoCompactAsync(output, CompactionReason.Threshold, token);
                    }
                    return execution;
                },
                cancellationToken);
        }
        finally
        {
            sessions.ExitTurn();
        }

        if (sessions.IsPersistent && sessions.Document?.IsPiV3 == true)
        {
            await sessions.PersistAgentStateCacheAsync(cancellationToken);
        }
        var eventType = result.Cancelled ? PiSharpExtensionEvent.TurnCancelled : PiSharpExtensionEvent.AfterTurn;
        await bootstrap.ExtensionHost.PublishAsync(eventType, context);
        output.AgentFinished(result.AssistantText, result.Cancelled);
        return result;
    }

    public static Func<string, string> CreateInputExpander(
        AgentBootstrap bootstrap,
        PiSharpExtensionHost extensionHost) =>
        text => PromptTemplateCatalog.Expand(
            SkillCatalog.ExpandCommand(extensionHost.TransformInput(text), bootstrap.Skills),
            bootstrap.PromptTemplates);

    private static async Task<TurnExecutionResult> RunSingleAsync(
        AIAgent agent,
        AgentSession session,
        string prompt,
        IChatOutput output,
        CancellationToken cancellationToken,
        IReadOnlyList<AIContent>? contents,
        RetryPolicyOptions retryPolicy,
        Func<JsonElement, Task>? persistMessage)
    {
        var response = new StringBuilder();
        output.AssistantMessageStarted();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var toolArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var toolRecords = new Dictionary<string, ToolExecutionRecord>(StringComparer.Ordinal);
        var assembler = new DurableMessageAssembler(persistMessage, toolNames);
        var retryNumber = 0;
        while (true)
        {
            try
            {
                var updates = contents is null
                    ? agent.RunStreamingAsync(prompt, session, cancellationToken: cancellationToken)
                    : agent.RunStreamingAsync(
                        [new ChatMessage(ChatRole.User, [new TextContent(prompt), .. contents])],
                        session,
                        cancellationToken: cancellationToken);
                await foreach (var update in updates)
                {
                    RenderToolContents(update, toolNames, toolArguments, toolRecords, output);
                    await assembler.ProcessAsync(update, cancellationToken);
                    if ((update.Role is null || update.Role == ChatRole.Assistant) && !string.IsNullOrEmpty(update.Text))
                    {
                        output.WriteText(update.Text);
                        response.Append(update.Text);
                    }
                }

                await assembler.CompleteAsync(cancellationToken);
                var assistantText = response.ToString();
                output.AssistantMessageFinished(assistantText);
                output.WriteLine();
                return new TurnExecutionResult(
                    assistantText,
                    ToolExecutions: toolRecords.Values.ToArray(),
                    DurableMessages: assembler.Messages);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var assistantText = response.ToString();
                output.AssistantMessageFinished(assistantText);
                output.WriteLine();
                return new TurnExecutionResult(
                    assistantText,
                    Cancelled: true,
                    ToolExecutions: toolRecords.Values.ToArray(),
                    DurableMessages: assembler.Messages);
            }
            catch (Exception exception) when (
                retryPolicy.Enabled &&
                retryNumber < retryPolicy.MaxRetries &&
                response.Length == 0 &&
                toolNames.Count == 0 &&
                !assembler.HasActivity &&
                RetryPolicy.IsTransient(exception, cancellationToken))
            {
                retryNumber++;
                await Task.Delay(RetryPolicy.GetDelay(retryNumber, retryPolicy), cancellationToken);
            }
        }
    }

    private static void RenderToolContents(
        AgentResponseUpdate update,
        IDictionary<string, string> toolNames,
        IDictionary<string, string> toolArguments,
        IDictionary<string, ToolExecutionRecord> toolRecords,
        IChatOutput output)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call when !call.InformationalOnly:
                    RenderToolCall(call, toolNames, toolArguments, toolRecords, output);
                    break;
                case FunctionResultContent result:
                    var name = toolNames.TryGetValue(result.CallId, out var knownName) ? knownName : result.CallId;
                    var resultElement = ToJsonElement(result.Result);
                    toolRecords[result.CallId] = toolRecords.TryGetValue(result.CallId, out var record)
                        ? record with { Result = resultElement, IsError = result.Exception is not null }
                        : new ToolExecutionRecord(
                            result.CallId,
                            name,
                            EmptyObject(),
                            resultElement,
                            result.Exception is not null);
                    output.ToolFinished(result.CallId, name, result.Exception?.Message, FormatValue(result.Result));
                    break;
            }
        }
    }

    private static void RenderToolCall(
        FunctionCallContent call,
        IDictionary<string, string> toolNames,
        IDictionary<string, string> toolArguments,
        IDictionary<string, ToolExecutionRecord> toolRecords,
        IChatOutput output)
    {
        var arguments = FormatJson(call.Arguments);
        var argumentsElement = ToJsonElement(call.Arguments);
        toolNames[call.CallId] = call.Name;
        toolRecords[call.CallId] = toolRecords.TryGetValue(call.CallId, out var existingRecord)
            ? existingRecord with { Name = call.Name, Arguments = argumentsElement }
            : new ToolExecutionRecord(call.CallId, call.Name, argumentsElement, null, false);
        if (toolArguments.TryGetValue(call.CallId, out var previousArguments))
        {
            if (!string.Equals(previousArguments, arguments, StringComparison.Ordinal))
            {
                output.ToolUpdated(call.CallId, call.Name, arguments);
                toolArguments[call.CallId] = arguments;
            }
            return;
        }

        toolArguments[call.CallId] = arguments;
        output.ToolStarted(call.CallId, call.Name, arguments);
    }

    private static string FormatJson(object? value)
    {
        if (value is null)
        {
            return "{}";
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (JsonException)
        {
            return value.ToString() ?? "{}";
        }
    }

    private static JsonElement ToJsonElement(object? value)
    {
        try
        {
            using var document = JsonDocument.Parse(FormatJson(value));
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string FormatValue(object? value)
    {
        var text = value switch
        {
            null => "(no result)",
            string stringValue => stringValue,
            EditToolResult edit => $"{edit.Message}\n{edit.Diff}",
            JsonElement element when element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("diff", out var diff) => diff.GetString() ?? element.ToString(),
            _ => FormatJson(value),
        };
        const int MaxToolPreviewCharacters = 4_000;
        return text.Length <= MaxToolPreviewCharacters
            ? text
            : $"{text[..MaxToolPreviewCharacters]}… [tool output truncated]";
    }

    private sealed class DurableMessageAssembler(
        Func<JsonElement, Task>? persistMessage,
        IReadOnlyDictionary<string, string> toolNames)
    {
        private readonly List<JsonElement> _messages = [];
        private readonly List<JsonElement> _content = [];
        private readonly Dictionary<string, int> _toolCallIndexes = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string> _toolNames = toolNames;
        private string? _messageId;
        private string? _responseId;
        private string? _modelId;
        private ChatFinishReason? _finishReason;
        private DateTimeOffset? _createdAt;

        public IReadOnlyList<JsonElement> Messages => _messages;

        public bool HasActivity { get; private set; }

        public async Task ProcessAsync(AgentResponseUpdate update, CancellationToken cancellationToken)
        {
            if (_messageId is not null && update.MessageId is not null &&
                !string.Equals(_messageId, update.MessageId, StringComparison.Ordinal))
            {
                await CompleteAsync(cancellationToken);
            }

            CaptureMetadata(update);
            var results = update.Contents.OfType<FunctionResultContent>().ToArray();
            if (results.Length > 0)
            {
                foreach (var content in update.Contents.Where(IsAssistantContent))
                {
                    HasActivity = true;
                    AppendContent(content);
                }
                await CompleteAsync(cancellationToken);
                foreach (var result in results)
                {
                    await AddToolResultAsync(result, update.CreatedAt ?? DateTimeOffset.UtcNow, cancellationToken);
                }
                return;
            }

            if (!update.Contents.Any(IsAssistantContent))
            {
                return;
            }

            foreach (var content in update.Contents.Where(IsAssistantContent))
            {
                HasActivity = true;
                AppendContent(content);
            }
        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            if (_content.Count == 0)
            {
                ResetCurrent();
                return;
            }

            var message = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = _content.ToArray(),
                ["timestamp"] = (_createdAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds(),
            };
            AddIfPresent(message, "responseId", _responseId);
            AddIfPresent(message, "model", _modelId);
            AddIfPresent(message, "stopReason", _finishReason?.ToString()?.ToLowerInvariant());
            await AddMessageAsync(JsonSerializer.SerializeToElement(message), cancellationToken);
            ResetCurrent();
        }

        private void ResetCurrent()
        {
            _content.Clear();
            _toolCallIndexes.Clear();
            _messageId = null;
            _responseId = null;
            _modelId = null;
            _finishReason = null;
            _createdAt = null;
        }

        private void CaptureMetadata(AgentResponseUpdate update)
        {
            _messageId ??= update.MessageId;
            _responseId = update.ResponseId ?? _responseId;
            _modelId = GetModelId(update) ?? _modelId;
            _finishReason = update.FinishReason ?? _finishReason;
            _createdAt ??= update.CreatedAt;
        }

        private void AppendContent(AIContent content)
        {
            switch (content)
            {
                case TextContent text:
                    AppendTextContent("text", "text", text.Text);
                    break;
                case TextReasoningContent reasoning:
                    AppendTextContent("thinking", "thinking", reasoning.Text);
                    break;
                case FunctionCallContent call when !call.InformationalOnly:
                    var callContent = JsonSerializer.SerializeToElement(new
                    {
                        type = "toolCall",
                        id = call.CallId,
                        name = call.Name,
                        arguments = call.Arguments,
                    });
                    if (_toolCallIndexes.TryGetValue(call.CallId, out var index))
                    {
                        _content[index] = callContent;
                    }
                    else
                    {
                        _toolCallIndexes[call.CallId] = _content.Count;
                        _content.Add(callContent);
                    }
                    break;
            }
        }

        private void AppendTextContent(string type, string propertyName, string text)
        {
            if (_content.Count > 0 && _content[^1].ValueKind == JsonValueKind.Object &&
                _content[^1].TryGetProperty("type", out var typeValue) &&
                string.Equals(typeValue.GetString(), type, StringComparison.Ordinal))
            {
                var previous = _content[^1].GetProperty(propertyName).GetString() ?? string.Empty;
                _content[^1] = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                {
                    ["type"] = type,
                    [propertyName] = previous + text,
                });
                return;
            }

            _content.Add(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = type,
                [propertyName] = text,
            }));
        }

        private async Task AddToolResultAsync(
            FunctionResultContent result,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken)
        {
            var message = JsonSerializer.SerializeToElement(new
            {
                role = "toolResult",
                toolCallId = result.CallId,
                toolName = _toolNames.GetValueOrDefault(result.CallId) ?? result.CallId,
                content = new[] { new { type = "text", text = FormatValue(result.Result) } },
                isError = result.Exception is not null,
                timestamp = timestamp.ToUnixTimeMilliseconds(),
            });
            await AddMessageAsync(message, cancellationToken);
        }

        private async Task AddMessageAsync(JsonElement message, CancellationToken cancellationToken)
        {
            var snapshot = message.Clone();
            _messages.Add(snapshot);
            if (persistMessage is not null)
            {
                await persistMessage(snapshot);
            }
        }

        private static bool IsAssistantContent(AIContent content) =>
            content is TextContent or TextReasoningContent or FunctionCallContent;

        private static string? GetModelId(AgentResponseUpdate update) =>
            update.RawRepresentation is ChatResponseUpdate response ? response.ModelId : null;

        private static void AddIfPresent(IDictionary<string, object?> target, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                target[key] = value;
            }
        }
    }
}
