using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcRetryCommandHandler(
    Func<JsonElement?, string, bool, string?, Task> respond,
    Func<ConversationRun> currentRun,
    Func<bool, CancellationToken, Task>? persistEnabled)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement root, JsonElement? id,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "set_auto_retry":
                if (!root.TryGetProperty("enabled", out var enabled) ||
                    enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    await respond(id, command, false, "enabled must be a boolean.");
                    return true;
                }
                try
                {
                    var value = enabled.GetBoolean();
                    if (persistEnabled is null) currentRun().SetAutoRetryEnabled(value);
                    else await persistEnabled(value, cancellationToken);
                    await respond(id, command, true, null);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await respond(id, command, false, error.Message);
                }
                return true;

            case "abort_retry":
                currentRun().AbortRetry();
                await respond(id, command, true, null);
                return true;

            default:
                return false;
        }
    }
}
