using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcCompactionCommandHandler(
    JsonLineWriter output,
    Func<JsonElement?, string, bool, string?, Task> respond,
    Func<ConversationRun> currentRun,
    Func<bool, CancellationToken, Task>? persistEnabled,
    Func<Task> cancelActiveRun,
    Func<AgentLifecycleEvent, Task> emitLifecycle)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement root, JsonElement? id,
        CancellationToken cancellationToken, Func<Task>? cancelBeforeCompact = null)
    {
        if (command == "compact")
        {
            if (root.TryGetProperty("customInstructions", out var instructions) &&
                instructions.ValueKind != JsonValueKind.String)
            {
                await respond(id, command, false, "customInstructions must be text.");
                return true;
            }

            try
            {
                await (cancelBeforeCompact ?? cancelActiveRun)();
                var result = await currentRun().CompactWithResultAsync(
                    root.TryGetProperty("customInstructions", out instructions) ? instructions.GetString() : null,
                    cancellationToken, emitLifecycle);
                if (result is null)
                {
                    var lastEntry = currentRun().Conversation.Tree.ActivePath()
                        .LastOrDefault(entry => entry.Type is not ("usage" or "context_projection"));
                    var error = lastEntry?.Type == "compaction"
                        ? "Already compacted"
                        : "Nothing to compact (session too small)";
                    await respond(id, command, false, error);
                    return true;
                }

                await output.EmitAsync(new
                {
                    id,
                    type = "response",
                    command,
                    success = true,
                    data = RpcCompactionResultProjector.Project(result)
                }, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await respond(id, command, false, error.Message);
            }
            return true;
        }

        if (command != "set_auto_compaction") return false;

        if (!root.TryGetProperty("enabled", out var enabledValue) ||
            enabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            await respond(id, command, false, "enabled must be a boolean.");
            return true;
        }

        try
        {
            var enabled = enabledValue.GetBoolean();
            if (persistEnabled is null) currentRun().SetAutoCompactionEnabled(enabled);
            else await persistEnabled(enabled, cancellationToken);
            await respond(id, command, true, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await respond(id, command, false, error.Message);
        }
        return true;
    }
}
