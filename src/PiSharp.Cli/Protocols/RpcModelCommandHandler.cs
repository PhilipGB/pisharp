using System.Text.Json;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcModelCommandHandler(
    JsonLineWriter writer,
    Func<JsonElement?, string, bool, string?, Task> respond,
    Func<RpcEventWriter> events,
    Func<string?, CancellationToken, Task<IReadOnlyList<ModelDescriptor>>>? discoverModels,
    Func<ModelDescriptor, CancellationToken, Task<ModelDescriptor>>? setModel,
    Func<ConversationRun> currentRun,
    Func<string?>? getThinkingLevel,
    Func<bool>? isModelScoped,
    Func<string, CancellationToken, Task<string>>? setThinkingLevel,
    Func<string, CancellationToken, Task<string>>? setThinkingLevelDuringRun,
    Func<IReadOnlyList<string>>? getAvailableThinkingLevels,
    Func<bool>? supportsThinking)
{
    public async Task<bool> TryHandleAsync(string command, JsonElement root, JsonElement? id, bool busy,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "get_available_models":
                if (busy) await respond(id, command, false, "Wait until the active prompt settles.");
                else if (discoverModels is null) await respond(id, command, false, "Model discovery is unavailable.");
                else
                {
                    try
                    {
                        var models = await discoverModels(null, cancellationToken);
                        await writer.EmitAsync(new
                        {
                            id,
                            type = "response",
                            command,
                            success = true,
                            data = new { models }
                        }, cancellationToken);
                    }
                    catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or TaskCanceledException)
                    {
                        await respond(id, command, false, error.Message);
                    }
                }
                return true;
            case "set_model":
                if (busy) { await respond(id, command, false, "Wait until the active prompt settles."); return true; }
                if (!root.TryGetProperty("provider", out var modelProvider) || modelProvider.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(modelProvider.GetString()) || !root.TryGetProperty("modelId", out var modelId) ||
                    modelId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(modelId.GetString()))
                { await respond(id, command, false, "A provider and modelId are required."); return true; }
                if (setModel is null) { await respond(id, command, false, "Model selection is unavailable."); return true; }
                if (discoverModels is null) { await respond(id, command, false, "Model discovery is unavailable."); return true; }
                try
                {
                    var requestedProvider = modelProvider.GetString()!;
                    var requestedModelId = modelId.GetString()!;
                    var models = await discoverModels(requestedProvider, cancellationToken);
                    var candidate = models.FirstOrDefault(model =>
                        model.Provider?.Equals(requestedProvider, StringComparison.Ordinal) == true &&
                        model.Id.Equals(requestedModelId, StringComparison.Ordinal));
                    if (candidate is null)
                    {
                        await respond(id, command, false, $"Model not found: {requestedProvider}/{requestedModelId}");
                        return true;
                    }
                    var selectedModel = await setModel(candidate, cancellationToken);
                    await writer.EmitAsync(new
                    {
                        id,
                        type = "response",
                        command,
                        success = true,
                        data = new { provider = selectedModel.Provider, id = selectedModel.Id }
                    }, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await respond(id, command, false, error.Message);
                }
                return true;
            case "cycle_model":
                if (busy) { await respond(id, command, false, "Wait until the active prompt settles."); return true; }
                if (discoverModels is null) { await respond(id, command, false, "Model discovery is unavailable."); return true; }
                try
                {
                    var models = (await discoverModels(null, cancellationToken))
                        .Where(model => model.Available && !string.IsNullOrWhiteSpace(model.Provider)).ToArray();
                    if (models.Length <= 1)
                    {
                        await writer.EmitAsync(new { id, type = "response", command, success = true, data = (object?)null }, cancellationToken);
                        return true;
                    }
                    if (setModel is null) { await respond(id, command, false, "Model selection is unavailable."); return true; }
                    var current = currentRun().Conversation;
                    var currentIndex = Array.FindIndex(models, model =>
                        model.Provider?.Equals(current.Provider, StringComparison.Ordinal) == true &&
                        model.Id.Equals(current.Model, StringComparison.Ordinal));
                    if (currentIndex < 0) currentIndex = 0;
                    var candidate = models[(currentIndex + 1) % models.Length];
                    var selectedModel = await setModel(candidate, cancellationToken);
                    await writer.EmitAsync(new
                    {
                        id,
                        type = "response",
                        command,
                        success = true,
                        data = new
                        {
                            model = new { provider = selectedModel.Provider, id = selectedModel.Id },
                            thinkingLevel = getThinkingLevel?.Invoke() ?? "off",
                            isScoped = isModelScoped?.Invoke() ?? false
                        }
                    }, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await respond(id, command, false, error.Message);
                }
                return true;
            case "set_thinking_level":
                if (!root.TryGetProperty("level", out var requestedLevel) || requestedLevel.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(requestedLevel.GetString()))
                { await respond(id, command, false, "A thinking level is required."); return true; }
                if (busy && setThinkingLevelDuringRun is null)
                { await respond(id, command, false, "Thinking-level changes during an active run are unavailable."); return true; }
                if (!busy && setThinkingLevel is null)
                { await respond(id, command, false, "Thinking-level selection is unavailable."); return true; }
                try
                {
                    var previousLevel = getThinkingLevel?.Invoke();
                    var setter = busy ? setThinkingLevelDuringRun! : setThinkingLevel!;
                    var selectedLevel = await setter(requestedLevel.GetString()!, cancellationToken);
                    if (!string.Equals(previousLevel, selectedLevel, StringComparison.Ordinal))
                        await events().EmitThinkingLevelChangedAsync(selectedLevel, cancellationToken);
                    await respond(id, command, true, null);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await respond(id, command, false, error.Message);
                }
                return true;
            case "cycle_thinking_level":
                if (supportsThinking?.Invoke() != true)
                {
                    await writer.EmitAsync(new { id, type = "response", command, success = true, data = (object?)null }, cancellationToken);
                    return true;
                }
                if ((busy && setThinkingLevelDuringRun is null) || (!busy && setThinkingLevel is null) ||
                    getAvailableThinkingLevels is null)
                { await respond(id, command, false, "Thinking-level selection is unavailable."); return true; }
                try
                {
                    var levels = getAvailableThinkingLevels();
                    if (levels.Count == 0)
                    {
                        await writer.EmitAsync(new { id, type = "response", command, success = true, data = (object?)null }, cancellationToken);
                        return true;
                    }
                    var currentLevel = getThinkingLevel?.Invoke() ?? "off";
                    var currentIndex = Array.IndexOf(levels.ToArray(), currentLevel);
                    var nextLevel = levels[(currentIndex + 1 + levels.Count) % levels.Count];
                    var setter = busy ? setThinkingLevelDuringRun! : setThinkingLevel!;
                    var selectedLevel = await setter(nextLevel, cancellationToken);
                    if (!string.Equals(currentLevel, selectedLevel, StringComparison.Ordinal))
                        await events().EmitThinkingLevelChangedAsync(selectedLevel, cancellationToken);
                    await writer.EmitAsync(new
                    {
                        id,
                        type = "response",
                        command,
                        success = true,
                        data = new { level = selectedLevel }
                    }, cancellationToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    await respond(id, command, false, error.Message);
                }
                return true;
            case "get_available_thinking_levels":
                if (getAvailableThinkingLevels is null)
                { await respond(id, command, false, "Thinking-level discovery is unavailable."); return true; }
                await writer.EmitAsync(new
                {
                    id,
                    type = "response",
                    command,
                    success = true,
                    data = new { levels = getAvailableThinkingLevels() }
                }, cancellationToken);
                return true;
            default:
                return false;
        }
    }
}
