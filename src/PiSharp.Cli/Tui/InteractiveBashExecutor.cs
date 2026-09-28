using PiSharp.Runtime.Tools;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

internal static class InteractiveBashExecutor
{
    public static bool TryParse(string input, out string command, out bool excludeFromContext)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.TrimStart();
        excludeFromContext = text.StartsWith("!!", StringComparison.Ordinal);
        if (!text.StartsWith('!'))
        {
            command = "";
            excludeFromContext = false;
            return false;
        }

        command = text[(excludeFromContext ? 2 : 1)..].Trim();
        return command.Length > 0;
    }

    public static async Task ExecuteAsync(ConversationRun run, string command, bool excludeFromContext,
        InteractiveTranscript transcript, Func<CancellationToken, Task>? persist, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(transcript);
        var operationId = Guid.NewGuid().ToString("N");
        transcript.Render(new AgentLifecycleEvent("tool_execution_started", Tool: "bash", OperationId: operationId)
        {
            ToolArguments = new Dictionary<string, object?>(StringComparer.Ordinal) { ["command"] = command }
        });

        BashExecutionResult result;
        try
        {
            result = await run.ExecuteBashAsync(command,
                delta => transcript.Render(new AgentLifecycleEvent("tool_execution_update", Text: delta,
                    Tool: "bash", OperationId: operationId)), cancellationToken);
        }
        catch (Exception error)
        {
            transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Tool: "bash",
                OperationId: operationId, IsError: true, Error: error.Message));
            return;
        }

        var recorded = run.RecordBashResult(command, result, excludeFromContext);
        if (recorded && persist is not null) await persist(cancellationToken);
        var failed = !result.Cancelled && result.ExitCode is { } exitCode && exitCode != 0;
        transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Text: result.DisplayOutput,
            Tool: "bash", OperationId: operationId, IsError: failed,
            Error: failed ? $"Command exited with code {result.ExitCode}" : null, Details: result));
    }
}
