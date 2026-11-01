namespace PiSharp.Runtime.Sessions;

/// <summary>
/// Tracks tool calls that have already done their work but have not yet published their completion.
///
/// Pinned Pi runs its agent loop and its input handling on one event loop, so once a tool call returns,
/// the completion is published before any accepted steering or follow-up can be observed. PiSharp runs
/// the run pipeline and the RPC command loop on different tasks, and the completion path awaits durable
/// persistence, so an accepted steer could be published before a tool that had already finished. Waiting
/// for this gate reproduces Pi's happens-before edge without delaying tools that are still running.
/// </summary>
internal sealed class ToolCompletionGate
{
    private readonly object _gate = new();
    private int _completing;
    private TaskCompletionSource? _idle;

    /// <summary>Marks a tool that has returned from its work and still has to publish its completion.</summary>
    public IDisposable Begin()
    {
        lock (_gate) _completing++;
        return new Scope(this);
    }

    /// <summary>Completes when no tool is between returning and publishing its completion.</summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            if (_completing == 0) return Task.CompletedTask;
            return (_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private void End()
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            _completing--;
            if (_completing == 0)
            {
                idle = _idle;
                _idle = null;
            }
        }
        idle?.TrySetResult();
    }

    private sealed class Scope(ToolCompletionGate owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End();
        }
    }
}
