using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Renders agent lifecycle events as normal-screen transcript output.</summary>
public sealed class InteractiveTranscript(TextWriter output, TextWriter status, bool interactive = true,
    bool hideThinking = false, TerminalScreen? screen = null,
    Func<string, PiSharpToolRenderer?>? toolRenderer = null, string? workingDirectory = null,
    Func<string, string?>? terminalEnvironment = null)
{
    private readonly Dictionary<string, ShellOutputNormalizer> _liveBash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StringBuilder> _liveBashOutput = new(StringComparer.Ordinal);
    private readonly StringBuilder _assistantMarkdown = new();
    private readonly StringBuilder _assistantThinking = new();
    private readonly TerminalToolPresentation _toolPresentation = new(toolRenderer,
        workingDirectory ?? Environment.CurrentDirectory, () => screen?.ToolResultsExpanded ?? false, terminalEnvironment);
    private bool _hideThinkingBlock = hideThinking;
    private string? _lastFinishReason;

    public bool HasAssistantOutput { get; private set; }
    public int ExitCode { get; private set; }

    public void SetHideThinkingBlock(bool hidden) => _hideThinkingBlock = hidden;

    public void LoadHistory(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!interactive || screen is null) return;

        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var nestedCalls = session.ActiveNestedToolCallsByResultCallId();
        var messages = session.ActiveMessages();
        var assistantTextNeedsTurnSeparator = false;
        screen.ReplaceTranscript(target =>
        {
            foreach (var message in messages)
            {
                if (message.Role == ChatRole.User)
                {
                    if (PiSharpCustomMessageProjection.TryRead(message, out var customMessage) &&
                        !customMessage.Display) continue;
                    if (assistantTextNeedsTurnSeparator) target.Output.WriteLine();
                    assistantTextNeedsTurnSeparator = false;
                    target.AppendUserMessage(MessageText(message), message.Contents.OfType<DataContent>().ToArray());
                    continue;
                }
                if (message.Role == ChatRole.Assistant)
                {
                    var thinking = new StringBuilder();
                    void FlushThinking()
                    {
                        if (thinking.Length == 0) return;
                        target.AppendAssistantThinkingBlock(thinking.ToString());
                        thinking.Clear();
                        assistantTextNeedsTurnSeparator = true;
                    }

                    foreach (var content in message.Contents)
                    {
                        if (content is TextReasoningContent reasoning && reasoning.Text.Length > 0)
                        {
                            thinking.Append(TerminalSafeText.Normalize(reasoning.Text));
                        }
                        else if (content is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                        {
                            FlushThinking();
                            target.CommitAssistantText(text.Text.Trim());
                            assistantTextNeedsTurnSeparator = true;
                        }
                        else if (content is FunctionCallContent call)
                        {
                            FlushThinking();
                            assistantTextNeedsTurnSeparator = false;
                            toolNames[call.CallId] = call.Name;
                            var update = new AgentLifecycleEvent("tool_execution_started", Tool: call.Name,
                                OperationId: call.CallId)
                            {
                                ToolArguments = call.Arguments is { } arguments
                                    ? new Dictionary<string, object?>(arguments, StringComparer.Ordinal) : null
                            };
                            target.AppendToolCall(_toolPresentation.RenderCall(update, expanded: false),
                                _toolPresentation.RenderCall(update, expanded: true), call.CallId);
                            if (call.Exception is not null)
                            {
                                var failed = new AgentLifecycleEvent("tool_execution_finished", Tool: call.Name,
                                    OperationId: call.CallId, IsError: true, Error: call.Exception.Message);
                                var views = _toolPresentation.RenderResultViews(failed, isError: true);
                                target.AppendToolResult(views.Collapsed, views.Expanded, operationId: call.CallId,
                                    isError: true);
                            }
                        }
                    }
                    FlushThinking();
                    continue;
                }
                if (message.Role != ChatRole.Tool) continue;
                assistantTextNeedsTurnSeparator = false;
                foreach (var result in message.Contents.OfType<FunctionResultContent>())
                {
                    var images = result.Result is IReadOnlyList<AIContent> parts
                        ? parts.OfType<DataContent>().ToArray() : [];
                    var name = toolNames.GetValueOrDefault(result.CallId, "tool");
                    var failed = result.Exception is not null;
                    var update = new AgentLifecycleEvent("tool_execution_finished", Text: ToolResultText(result.Result),
                        Tool: name, OperationId: result.CallId, IsError: failed,
                        Error: result.Exception?.Message, Details: result.Result)
                    {
                        DurationMs = ToolExecutionDuration.Get(result)
                    };
                    var views = _toolPresentation.RenderResultViews(update, failed);
                    if (name == "codemode") target.CompleteToolRow(result.CallId, views.Collapsed, views.Expanded, failed, images);
                    else target.AppendToolResult(views.Collapsed, views.Expanded, images,
                        suppressUnsupportedImageFallbackWhenCollapsed: name == "read", operationId: result.CallId,
                        isError: failed);
                    if (nestedCalls.TryGetValue(result.CallId, out var nested))
                        target.AppendToolResult(RenderNestedToolCalls(nested));
                }
            }
        });
        screen.MarkHistoryLoaded(assistantTextNeedsTurnSeparator);
    }

    public void Render(AgentLifecycleEvent update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Type is not ("model_text_delta" or "model_content_update") && !DeferAssistantCommit(update))
        {
            CommitAssistantThinkingText();
            CommitAssistantText();
        }
        switch (update.Type)
        {
            case "prompt_accepted" when !interactive:
                _lastFinishReason = null;
                break;
            case "model_request_completed" when !interactive:
                _lastFinishReason = update.ProviderFinishReason;
                break;
            case "prompt_accepted" when interactive && screen is not null:
                if (update.PromptMessage is { } promptMessage &&
                    PiSharpCustomMessageProjection.TryRead(promptMessage, out var customMessage) &&
                    !customMessage.Display) break;
                screen.AppendUserMessage(Safe(update.Text ?? ""), update.Images);
                break;
            case "model_text_delta" when !string.IsNullOrEmpty(update.Text):
                CommitAssistantThinkingText();
                var assistantText = Safe(update.Text);
                if (screen is null) output.Write(assistantText);
                else
                {
                    _assistantMarkdown.Append(assistantText);
                    screen.SetAssistantText(_assistantMarkdown.ToString());
                }
                HasAssistantOutput = true;
                break;
            case "reasoning_delta" when interactive && !string.IsNullOrEmpty(update.Text):
                CommitAssistantText();
                var reasoning = Safe(update.Text);
                if (screen is null)
                {
                    if (!_hideThinkingBlock) status.Write(reasoning);
                }
                else
                {
                    _assistantThinking.Append(reasoning);
                    screen.SetAssistantThinkingText(_assistantThinking.ToString());
                }
                HasAssistantOutput = true;
                break;
            case "usage" when interactive && !string.IsNullOrEmpty(update.Text):
                if (screen is null) status.WriteLine($"\nUsage: {Safe(update.Text)}");
                break;
            case "context_compacted" when interactive && !string.IsNullOrEmpty(update.Text):
                status.WriteLine(Safe(update.Text));
                break;
            case "auto_retry_start":
                {
                    var attempt = update.RetryAttempt?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                    var maxAttempts = update.RetryMaxAttempts?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                    var delay = update.RetryDelayMs is { } delayMs
                        ? $" in {(delayMs / 1000d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}s" : "";
                    var error = string.IsNullOrWhiteSpace(update.Error) ? "" : $": {Safe(update.Error)}";
                    status.WriteLine($"Retrying provider request ({attempt}/{maxAttempts}){delay}{error}");
                    break;
                }
            case "auto_retry_end" when update.RetrySuccess == true:
                status.WriteLine("Provider retry succeeded.");
                break;
            case "auto_retry_end" when update.RetrySuccess == false:
                {
                    var finalError = string.IsNullOrWhiteSpace(update.RetryFinalError)
                        ? "" : $": {Safe(update.RetryFinalError)}";
                    status.WriteLine($"Provider retry stopped{finalError}");
                    break;
                }
            case "tool_execution_started" when interactive:
                WriteToolCall(update);
                break;
            case "tool_execution_update" when interactive && update.Tool == "bash" && !string.IsNullOrEmpty(update.Text):
                WriteBashUpdate(update);
                break;
            case "tool_execution_update" when interactive && update.Tool == "codemode" && screen is not null &&
                (update.OperationId ?? update.ToolCallId) is { } progressId:
                screen.UpdateToolTimer(progressId, update.IsError == true);
                break;
            case "tool_execution_finished" when interactive:
                FinishTool(update);
                break;
            case "turn_failed" or "prompt_rejected":
                screen?.ClearToolTimers();
                ExitCode = Math.Max(ExitCode, 1);
                status.WriteLine($"Agent error: {Safe(update.Error ?? update.Type)}");
                break;
            case "turn_interrupted":
                screen?.ClearToolTimers();
                if (screen is null) status.WriteLine("Interrupted.");
                else screen.AppendThemedTranscriptLine("Operation aborted", "error", isError: true);
                break;
        }
    }

    public void FinishTurn()
    {
        screen?.ClearToolTimers();
        CommitAssistantThinkingText();
        CommitAssistantText();
        if (!interactive && _lastFinishReason == "length")
        {
            status.WriteLine("Response was truncated: the model reached its output token limit.");
            ExitCode = Math.Max(ExitCode, 1);
        }
        if (!HasAssistantOutput) return;
        if (screen is null) output.WriteLine();
        HasAssistantOutput = false;
    }

    private void CommitAssistantText()
    {
        if (_assistantMarkdown.Length == 0) return;
        if (screen is not null) screen.CommitAssistantText(_assistantMarkdown.ToString());
        _assistantMarkdown.Clear();
    }

    private void CommitAssistantThinkingText()
    {
        if (_assistantThinking.Length == 0) return;
        if (screen is not null) screen.CommitAssistantThinkingText(_assistantThinking.ToString());
        _assistantThinking.Clear();
    }

    private bool DeferAssistantCommit(AgentLifecycleEvent update)
    {
        if (screen is null || _assistantMarkdown.Length == 0) return false;
        if (update.Type is "usage" or "reasoning_delta") return true;
        if (update.Type == "model_request_completed")
            return !HasToolCalls(update.ProviderResponse);
        if (update.Type == "assistant_turn_completed")
            return !HasToolCalls(update.TurnMessage);
        return false;
    }

    private static bool HasToolCalls(ChatMessage? message) =>
        message?.Contents.OfType<FunctionCallContent>().Any() == true;

    private static string MessageText(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));

    private static string ToolResultText(object? result)
    {
        if (result is null) return "";
        if (result is string text) return text;
        if (result is IEnumerable<AIContent> content)
            return string.Concat(content.OfType<TextContent>().Select(item => item.Text));
        try { return JsonSerializer.Serialize(result); }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return result.ToString() ?? "";
        }
    }

    private void WriteBashUpdate(AgentLifecycleEvent update)
    {
        var text = update.Text!;
        if (update.OperationId is { } id)
        {
            if (!_liveBash.TryGetValue(id, out var normalizer))
                _liveBash.Add(id, normalizer = new ShellOutputNormalizer());
            text = normalizer.Append(text.AsSpan());
            if (screen is not null)
            {
                if (!_liveBashOutput.TryGetValue(id, out var accumulated))
                    _liveBashOutput.Add(id, accumulated = new StringBuilder());
                AppendBounded(accumulated, text);
                var partial = update with { Text = accumulated.ToString() };
                screen.SetToolTimerRenderer(id, timerText => RenderBashPartial(partial, timerText));
                var timerText = screen.ProgressToolTimerInOutput(id);
                var views = RenderBashPartial(partial, timerText);
                screen.UpdateToolRow(id, views.Collapsed, views.Expanded, isError: false);
                return;
            }
        }
        else text = ShellOutputNormalizer.NormalizeComplete(text);
        if (text.Length != 0) status.Write(Safe(text));
    }

    private void FinishTool(AgentLifecycleEvent update)
    {
        if (update.Tool == "bash" && update.OperationId is { } id && screen is not null)
        {
            var output = update.Text is { Length: > 0 } completeOutput
                ? ShellOutputNormalizer.NormalizeComplete(completeOutput)
                : _liveBashOutput.TryGetValue(id, out var accumulated) ? accumulated.ToString() : "";
            if (_liveBash.Remove(id, out var normalizer) && update.Text is not { Length: > 0 })
                output += normalizer.Finish();
            _liveBashOutput.Remove(id);
            WriteToolResultViews(update with { Text = output });
        }
        else if (update.Tool == "bash" && update.OperationId is { } legacyId)
        {
            if (_liveBash.Remove(legacyId, out var normalizer))
            {
                var trailing = normalizer.Finish();
                if (trailing.Length != 0) status.Write(Safe(trailing));
            }
            else if (update.Text is { Length: > 0 } output)
            {
                status.Write(Safe(ShellOutputNormalizer.NormalizeComplete(output)));
            }
            status.WriteLine();
            var error = update.Error;
            var statusStart = error?.LastIndexOf("\n\nCommand ", StringComparison.Ordinal) ?? -1;
            if (statusStart >= 0) error = error![(statusStart + 2)..];
            if (error is not null) error = ShellOutputNormalizer.NormalizeComplete(error);
            status.WriteLine($"← {(update.IsError == true ? Safe(error ?? "bash failed") : "bash completed")}");
        }
        else
        {
            WriteToolResultViews(update);
        }
        if (update.NestedToolCalls is { } nestedCalls) WriteToolResult(RenderNestedToolCalls(nestedCalls), null);
    }

    private static PiSharpToolRenderView RenderNestedToolCalls(PiSharpNestedToolCalls nestedCalls)
    {
        var spans = new List<PiSharpToolTextSpan>
        {
            new("Nested tool calls", PiSharpToolTextStyle.Title, Bold: true)
        };
        foreach (var call in nestedCalls.Calls.Take(32))
        {
            var depth = Math.Clamp(call.Id.Count(character => character == '/'), 1, 8);
            spans.Add(new("\n" + new string(' ', depth * 2) + "↳ " + call.Name, PiSharpToolTextStyle.Output));
            spans.Add(new(" [" + call.Status + "]", call.Status == "error" ? PiSharpToolTextStyle.Error : PiSharpToolTextStyle.Muted));
            if (call.Error is not null)
                spans.Add(new("\n" + new string(' ', depth * 2 + 2) + call.Error, PiSharpToolTextStyle.Error));
        }
        if (nestedCalls.Calls.Count > 32)
            spans.Add(new($"\n… {nestedCalls.Calls.Count - 32} additional nested calls", PiSharpToolTextStyle.Muted));
        if (!nestedCalls.Complete) spans.Add(new("\nNested-call record is incomplete.", PiSharpToolTextStyle.Muted));
        return new PiSharpToolRenderView(spans);
    }

    private void WriteToolCall(AgentLifecycleEvent update)
    {
        if (!interactive) return;
        if ((update.Tool is "codemode" or "bash") && (update.OperationId ?? update.ToolCallId) is { } id)
            screen?.StartToolTimer(id);
        var collapsed = _toolPresentation.RenderCall(update, expanded: false);
        var expanded = _toolPresentation.RenderCall(update, expanded: true);
        if (screen is null)
            status.WriteLine(Environment.NewLine + TerminalToolPresentation.Render(collapsed, theme: null));
        else if (update.Tool == "bash" && (update.OperationId ?? update.ToolCallId) is { Length: > 0 } bashId)
        {
            var partial = update with { Text = "" };
            screen.SetToolTimerRenderer(bashId, timerText => RenderBashPartial(partial, timerText));
            var timerText = screen.ProgressToolTimerInOutput(bashId);
            var views = RenderBashPartial(partial, timerText);
            screen.AppendToolCall(views.Collapsed, views.Expanded, bashId);
        }
        else screen.AppendToolCall(collapsed, expanded, update.OperationId ?? update.ToolCallId);
    }

    private void WriteToolResult(PiSharpToolRenderView view, IReadOnlyList<Microsoft.Extensions.AI.DataContent>? images)
    {
        if (screen is null) status.WriteLine(TerminalToolPresentation.Render(view, theme: null));
        else screen.AppendToolResult(view, images);
    }

    private void WriteToolResultViews(AgentLifecycleEvent update)
    {
        var timed = (update.Tool is "codemode" or "bash") && screen is not null &&
            (update.OperationId ?? update.ToolCallId) is not null;
        var id = update.OperationId ?? update.ToolCallId;
        if (timed) update = update with { DurationMs = screen!.CompleteToolTimer(id!, update.DurationMs) };
        var views = _toolPresentation.RenderResultViews(update, update.IsError == true);
        if (screen is null)
            status.WriteLine(TerminalToolPresentation.Render(views.Collapsed, theme: null));
        else if (timed) screen.CompleteToolRow(id!, views.Collapsed, views.Expanded, update.IsError == true, update.Images);
        else screen.AppendToolResult(views.Collapsed, views.Expanded, update.Images,
            suppressUnsupportedImageFallbackWhenCollapsed: update.Tool == "read",
            operationId: id, isError: update.IsError == true);
    }

    private static void AppendBounded(StringBuilder output, string text)
    {
        const int maximum = 32_768;
        if (text.Length == 0 || output.Length >= maximum) return;
        var remaining = maximum - output.Length;
        output.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
    }

    private (PiSharpToolRenderView Collapsed, PiSharpToolRenderView Expanded) RenderBashPartial(
        AgentLifecycleEvent partial, string? progressText) =>
        _toolPresentation.RenderResultViews(partial, isError: false, retainArguments: true,
            progressText: progressText);

    private string Safe(string value) => interactive ? TerminalSafeText.Normalize(value) : value;
}

internal static class TerminalSafeText
{
    public static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == '\n') result.Append('\n');
            else if (rune.Value == '\t') result.Append("    ");
            else if (!Rune.IsControl(rune)) result.Append(rune.ToString());
        }
        return result.ToString();
    }
}

internal static class ToolDetailsSummary
{
    public static bool TryFormat(object details, out string summary)
    {
        summary = "";
        try
        {
            var root = details is JsonElement element ? element : JsonSerializer.SerializeToElement(details);
            if (root.ValueKind != JsonValueKind.Object) return false;
            var parts = new List<string>();
            AddLimit(root, "matchLimitReached", "match limit reached", parts);
            AddLimit(root, "resultLimitReached", "result limit reached", parts);
            AddLimit(root, "entryLimitReached", "entry limit reached", parts);
            if (TryProperty(root, "linesTruncated", out var linesTruncated) && linesTruncated.ValueKind == JsonValueKind.True)
                parts.Add("one or more matching lines were truncated");
            if (TryProperty(root, "truncation", out var truncation) && truncation.ValueKind == JsonValueKind.Object &&
                TryProperty(truncation, "truncated", out var wasTruncated) && wasTruncated.ValueKind == JsonValueKind.True)
            {
                var by = GetString(truncation, "truncatedBy");
                var totalBytes = GetInteger(truncation, "totalBytes");
                var outputBytes = GetInteger(truncation, "outputBytes");
                var totalLines = GetInteger(truncation, "totalLines");
                var outputLines = GetInteger(truncation, "outputLines");
                var reason = by is null ? "output truncated" : $"output truncated by {by}";
                if (totalBytes is not null && outputBytes is not null)
                    reason += $" ({outputBytes} of {totalBytes} bytes shown)";
                else if (totalLines is not null && outputLines is not null)
                    reason += $" ({outputLines} of {totalLines} lines shown)";
                parts.Add(reason);
            }
            if (parts.Count == 0) return false;
            summary = string.Join("; ", parts);
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void AddLimit(JsonElement root, string property, string label, ICollection<string> parts)
    {
        if (!TryProperty(root, property, out var value)) return;
        var limit = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : (long?)null;
        parts.Add(limit is null ? label : $"{label} ({limit})");
    }

    private static long? GetInteger(JsonElement root, string name) => TryProperty(root, name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static string? GetString(JsonElement root, string name) => TryProperty(root, name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        value = default;
        return false;
    }
}
