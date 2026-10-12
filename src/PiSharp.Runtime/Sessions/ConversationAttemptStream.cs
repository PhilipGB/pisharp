using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Consumes one MAF stream, checkpoints progress and preserves failure/disposal semantics.</summary>
internal sealed class ConversationAttemptStream
{
    private int _lastProgress;
    private DateTimeOffset _lastProgressAt = DateTimeOffset.UtcNow;
    public StringBuilder PartialText { get; } = new();
    public List<string> Events { get; } = [];
    public string InterruptionType { get; set; } = "turn_interrupted";
    public string? InterruptionError { get; set; }

    public async IAsyncEnumerable<AgentResponseUpdate> PumpAsync(IAsyncEnumerable<AgentResponseUpdate> updates,
        DurableExecution? durable, Action<UsageDetails> recordUsage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var updateEnumerator = updates.GetAsyncEnumerator(cancellationToken);
        Exception? streamError = null;
        try
        {
            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    if (!await updateEnumerator.MoveNextAsync()) break;
                    update = updateEnumerator.Current;
                }
                catch (OperationCanceledException error)
                {
                    streamError = error;
                    InterruptionType = "turn_interrupted";
                    InterruptionError = "Request was aborted";
                    throw;
                }
                catch (Exception error) when (cancellationToken.IsCancellationRequested)
                {
                    streamError = error;
                    InterruptionType = "turn_interrupted";
                    InterruptionError = "Request was aborted";
                    throw new OperationCanceledException("Request was aborted", error, cancellationToken);
                }
                catch (Exception error)
                {
                    streamError = error;
                    InterruptionType = "turn_failed";
                    InterruptionError = error.Message;
                    throw;
                }
                if (!string.IsNullOrEmpty(update.Text))
                {
                    PartialText.Append(update.Text);
                    if (durable is not null && (PartialText.Length - _lastProgress >= 512 ||
                        DateTimeOffset.UtcNow - _lastProgressAt >= TimeSpan.FromSeconds(2)))
                    {
                        await durable.ProgressAsync(PartialText.ToString());
                        _lastProgress = PartialText.Length;
                        _lastProgressAt = DateTimeOffset.UtcNow;
                    }
                }
                if (update.Contents is not null)
                    foreach (var content in update.Contents)
                        if (content is FunctionCallContent call) Events.Add($"call:{call.Name}:{call.CallId}");
                        else if (content is FunctionResultContent result) Events.Add($"result:{result.CallId}:{(result.Exception is null ? "ok" : "error")}");
                        else if (content is UsageContent usage)
                            recordUsage(usage.Details);
                yield return update;
            }
        }
        finally
        {
            try { await updateEnumerator.DisposeAsync(); }
            catch when (streamError is not null) { }
        }
    }
}
