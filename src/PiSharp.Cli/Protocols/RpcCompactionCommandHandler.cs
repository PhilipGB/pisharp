using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcCompactionCommandHandler(
    Func<JsonElement?, string, bool, string?, Task> respond,
    Func<ConversationRun> currentRun,
    Func<bool, CancellationToken, Task>? persistEnabled)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement root, JsonElement? id,
        CancellationToken cancellationToken)
    {
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
