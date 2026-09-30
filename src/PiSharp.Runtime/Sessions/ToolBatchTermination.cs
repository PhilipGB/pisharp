using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

internal sealed class ToolBatchTermination
{
    private readonly ConditionalWeakTable<IList<ChatMessage>, Batches> _requests = new();

    public bool Record(FunctionInvocationContext context, bool terminate)
    {
        var batches = _requests.GetValue(context.Messages, _ => new());
        lock (batches)
        {
            if (!batches.Iterations.TryGetValue(context.Iteration, out var batch))
                batches.Iterations[context.Iteration] = batch = new(context.FunctionCount);
            if (!batch.Finished.Add(context.FunctionCallIndex)) return false;
            batch.AllTerminate &= terminate;
            if (batch.Finished.Count != batch.Count) return false;
            batches.Iterations.Remove(context.Iteration);
            // MEAI stops when any invocation terminates; Pi requires every finalized sibling.
            return batch.AllTerminate;
        }
    }

    private sealed class Batches
    {
        public Dictionary<int, Batch> Iterations { get; } = [];
    }

    private sealed class Batch(int count)
    {
        public int Count { get; } = count;
        public HashSet<int> Finished { get; } = [];
        public bool AllTerminate { get; set; } = true;
    }
}
