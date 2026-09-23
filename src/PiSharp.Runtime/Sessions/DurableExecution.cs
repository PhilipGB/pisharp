using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Serializes independent tool checkpoints, including parallel MAF tool invocations.</summary>
internal sealed class DurableExecution(ConversationSession conversation, Func<CancellationToken, Task> save)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public Task StartAsync(string prompt, CancellationToken token) => CheckpointAsync("run_started",
        new { runId = RunId, prompt }, token);

    public Task ProgressAsync(string text) => CheckpointAsync("assistant_progress",
        new { runId = RunId, text }, CancellationToken.None);

    public async Task<string> StartToolAsync(string name, AIFunctionArguments arguments, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        // Failure to serialize arguments must prevent execution, not leave an unrecorded side effect.
        var args = JsonSerializer.SerializeToElement(arguments.ToDictionary(pair => pair.Key, pair => pair.Value));
        try { await CheckpointAsync("tool_intent", new { runId = RunId, operationId = id, name, arguments = args }, token); }
        catch
        {
            // The wrapper has not invoked the tool. This marker can only be persisted by a
            // later successful checkpoint; a process crash before then remains conservative.
            await _gate.WaitAsync(CancellationToken.None);
            try { conversation.Tree.Append("tool_skipped", JsonSerializer.SerializeToElement(new { runId = RunId, operationId = id })); }
            finally { _gate.Release(); }
            throw;
        }
        return id;
    }

    public Task EndToolAsync(string id, object? result, Exception? error) => CheckpointAsync("tool_outcome",
        new { runId = RunId, operationId = id, result = result?.ToString(), error = error?.Message }, CancellationToken.None);

    public Task FinishAsync(bool completed) => CheckpointAsync("run_finished",
        new { runId = RunId, completed }, CancellationToken.None);

    private async Task CheckpointAsync(string type, object payload, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            conversation.Tree.Append(type, JsonSerializer.SerializeToElement(payload));
            await save(CancellationToken.None);
        }
        finally { _gate.Release(); }
    }
}
