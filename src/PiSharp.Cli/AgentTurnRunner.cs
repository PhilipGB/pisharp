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
        await bootstrap.ExtensionHost.PublishAsync(PiSharpExtensionEvent.BeforeTurn, context);
        var result = await liveTurns.RunAsync(
            prompt,
            (message, token) =>
            {
                var contents = initial ? initialContents : null;
                initial = false;
                return RunSingleAsync(
                    bootstrap.Agent,
                    sessions.Session,
                    expandInput(message),
                    output,
                    token,
                    contents);
            },
            cancellationToken);
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
        IReadOnlyList<AIContent>? contents)
    {
        var response = new StringBuilder();
        output.AssistantMessageStarted();
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var toolArguments = new Dictionary<string, string>(StringComparer.Ordinal);
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
                RenderToolContents(update, toolNames, toolArguments, output);
                if ((update.Role is null || update.Role == ChatRole.Assistant) && !string.IsNullOrEmpty(update.Text))
                {
                    output.WriteText(update.Text);
                    response.Append(update.Text);
                }
            }

            var assistantText = response.ToString();
            output.AssistantMessageFinished(assistantText);
            output.WriteLine();
            return new TurnExecutionResult(assistantText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var assistantText = response.ToString();
            output.AssistantMessageFinished(assistantText);
            output.WriteLine();
            return new TurnExecutionResult(assistantText, Cancelled: true);
        }
    }

    private static void RenderToolContents(
        AgentResponseUpdate update,
        IDictionary<string, string> toolNames,
        IDictionary<string, string> toolArguments,
        IChatOutput output)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call when !call.InformationalOnly:
                    RenderToolCall(call, toolNames, toolArguments, output);
                    break;
                case FunctionResultContent result:
                    var name = toolNames.TryGetValue(result.CallId, out var knownName) ? knownName : result.CallId;
                    output.ToolFinished(result.CallId, name, result.Exception?.Message, FormatValue(result.Result));
                    break;
            }
        }
    }

    private static void RenderToolCall(
        FunctionCallContent call,
        IDictionary<string, string> toolNames,
        IDictionary<string, string> toolArguments,
        IChatOutput output)
    {
        var arguments = FormatJson(call.Arguments);
        toolNames[call.CallId] = call.Name;
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
}
