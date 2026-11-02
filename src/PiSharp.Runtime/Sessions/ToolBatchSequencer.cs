using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Serializes every invocation in a batch that contains a sequential tool.</summary>
internal sealed class ToolBatchSequencer
{
    private readonly ConditionalWeakTable<IList<ChatMessage>, Batches> _requests = new();

    public async ValueTask<IDisposable?> EnterAsync(FunctionInvocationContext context, bool sequential,
        CancellationToken cancellationToken)
    {
        if (!sequential || context.FunctionCount <= 1) return null;
        var batches = _requests.GetValue(context.Messages, _ => new());
        Batch batch;
        lock (batches)
            batch = batches.Iterations.TryGetValue(context.Iteration, out var existing)
                ? existing
                : batches.Iterations[context.Iteration] = new(context.FunctionCount);
        await batch.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(batches, context.Iteration, batch);
    }

    private sealed class Batches
    {
        public Dictionary<int, Batch> Iterations { get; } = [];
    }

    private sealed class Batch(int count)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Remaining { get; set; } = count;
    }

    private sealed class Lease(Batches batches, int iteration, Batch batch) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            batch.Gate.Release();
            var finished = false;
            lock (batches)
            {
                batch.Remaining--;
                if (batch.Remaining == 0)
                {
                    batches.Iterations.Remove(iteration);
                    finished = true;
                }
            }
            if (finished) batch.Gate.Dispose();
        }
    }
}
