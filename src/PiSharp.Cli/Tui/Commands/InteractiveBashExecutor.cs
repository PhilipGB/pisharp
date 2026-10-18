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
            Task EmitUpdateAsync(string delta)
            {
                transcript.Render(new AgentLifecycleEvent("tool_execution_update", Text: delta,
                    Tool: "bash", OperationId: operationId));
                return Task.CompletedTask;
            }

            var interception = await run.InterceptUserBashAsync(command, excludeFromContext, EmitUpdateAsync,
                cancellationToken);
            result = interception?.Kind switch
            {
                null => await run.ExecuteBashAsync(command, EmitUpdate, cancellationToken),
                PiSharp.Extensions.Abstractions.PiSharpUserBashResultKind.FullResult =>
                    new BashExecutionResult(interception.Output ?? "", interception.Output ?? "", interception.ExitCode,
                        interception.Cancelled, interception.Truncated, interception.FullOutputPath),
                PiSharp.Extensions.Abstractions.PiSharpUserBashResultKind.CustomOperation
                    when interception.CustomOperation is { } operation =>
                    await run.ExecuteUserBashOperationAsync(command, operation, EmitUpdate, cancellationToken),
                _ => throw new InvalidOperationException("The user_bash handler returned an invalid result.")
            };

            void EmitUpdate(string delta) => transcript.Render(new AgentLifecycleEvent("tool_execution_update", Text: delta,
                Tool: "bash", OperationId: operationId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new BashExecutionResult("", "(no output)", null, true, false, null);
        }
        catch (Exception error)
        {
            transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Tool: "bash",
                OperationId: operationId, IsError: true, Error: error.Message));
            return;
        }

        var recorded = run.RecordBashResult(command, result, excludeFromContext);
        if (recorded && persist is not null) await persist(CancellationToken.None);
        var failed = !result.Cancelled && result.ExitCode is { } exitCode && exitCode != 0;
        transcript.Render(new AgentLifecycleEvent("tool_execution_finished", Text: result.DisplayOutput,
            Tool: "bash", OperationId: operationId, IsError: failed,
            Error: failed ? $"Command exited with code {result.ExitCode}" : null, Details: result));
    }
}
