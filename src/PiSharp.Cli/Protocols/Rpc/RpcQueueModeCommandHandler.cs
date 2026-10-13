using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcQueueModeCommandHandler(
    Func<JsonElement?, string, bool, string?, Task> respond,
    Func<ConversationRun> currentRun,
    Func<bool, PromptDeliveryMode, CancellationToken, Task>? persistMode)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement root, JsonElement? id,
        CancellationToken cancellationToken)
    {
        var steering = command switch
        {
            "set_steering_mode" => true,
            "set_follow_up_mode" => false,
            _ => (bool?)null
        };
        if (steering is null) return false;

        if (!root.TryGetProperty("mode", out var modeValue) || modeValue.ValueKind != JsonValueKind.String ||
            !PromptDeliveryModes.TryParseSettingValue(modeValue.GetString(), out var mode))
        {
            await respond(id, command, false, "mode must be 'all' or 'one-at-a-time'.");
            return true;
        }

        try
        {
            if (persistMode is not null) await persistMode(steering.Value, mode, cancellationToken);
            else if (steering.Value) currentRun().SetSteeringMode(mode);
            else currentRun().SetFollowUpMode(mode);
            await respond(id, command, true, null);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await respond(id, command, false, error.Message);
        }
        return true;
    }
}
