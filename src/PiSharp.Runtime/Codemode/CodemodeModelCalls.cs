using System.Diagnostics;
using System.Text.Json;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Codemode;

internal sealed class CodemodeModelCalls(ICodemodeModels models, Action<AgentLifecycleEvent> publish, CodemodeUsageLedger ledger, Action<string> registerOrder)
{
    private readonly object _gate = new();
    private readonly List<PiSharpNestedToolCall> _calls = [];
    private readonly SemaphoreSlim _limit = new(4, 4);

    public IReadOnlyList<PiSharpNestedToolCall> Calls { get { lock (_gate) return _calls.ToArray(); } }

    public async Task<ClassifierResult> ClassifyAsync(string parentId, string provider, string model,
        ClassifierContext context, CancellationToken cancellationToken)
    {
        string id;
        int index;
        var started = Stopwatch.GetTimestamp();
        var arguments = JsonSerializer.SerializeToElement(new { provider, id = model });
        lock (_gate)
        {
            index = _calls.Count;
            id = $"{parentId}/models.classify/{index + 1}";
            _calls.Add(new(id, "models.classify", parentId, "unfinished", arguments));
        }
        registerOrder(id);
        publish(new("tool_execution_started", Tool: "models.classify")
        { ToolCallId = id, ParentToolCallId = parentId, ToolArguments = new Dictionary<string, object?> { ["provider"] = provider, ["id"] = model } });
        ClassifierResult result;
        try
        {
            await _limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { cancellationToken.ThrowIfCancellationRequested(); result = await models.ClassifyAsync(provider, model, context, cancellationToken).ConfigureAwait(false); }
            finally { _limit.Release(); }
        }
        catch (Exception error)
        {
            lock (_gate) _calls[index] = _calls[index] with { Status = cancellationToken.IsCancellationRequested ? "cancelled" : "error", Error = Preview(error.Message), DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds };
            publish(new("tool_execution_finished", Tool: "models.classify", IsError: true, Error: Preview(error.Message)) { ToolCallId = id, ParentToolCallId = parentId });
            throw;
        }
        var status = result.StopReason == "stop" ? "ok" : result.StopReason == "aborted" ? "cancelled" : "error";
        lock (_gate)
        {
            _calls[index] = _calls[index] with { Status = status, Error = Preview(result.ErrorMessage), DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, Cost = result.Usage?.Cost };
        }
        if (result.Usage is { } billed && ledger.Add(id, billed))
            publish(new("nested_model_usage", Cost: billed.Cost, TotalTokens: billed.TotalTokens) { UsageSnapshot = billed, ToolCallId = id, ParentToolCallId = parentId });
        publish(new("tool_execution_finished", Tool: "models.classify", IsError: status != "ok", Error: Preview(result.ErrorMessage), Cost: result.Usage?.Cost) { ToolCallId = id, ParentToolCallId = parentId });
        return result;
    }

    private static string? Preview(string? message) => message is { Length: > 500 } ? message[..500] : message;
}
