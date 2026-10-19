using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Extensions;

/// <summary>Owns direct executions for one loaded package without retaining package code.</summary>
internal sealed class NativeExtensionProcessExecutor(string workingDirectory) : PiSharpExtensionProcessExecutor, IDisposable
{
    private readonly string _workingDirectory = Path.GetFullPath(workingDirectory);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _running = [];
    private bool _disposed;

    public override Task<PiSharpExtensionExecResult> ExecuteAsync(string command, IReadOnlyList<string> args,
        PiSharpExtensionExecOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var directory = options?.WorkingDirectory is { } requested
                ? Path.GetFullPath(requested, _workingDirectory) : _workingDirectory;
            var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var task = ExecuteOwnedAsync(command, args, directory, options?.Timeout, operation);
            _running.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_gate) _running.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private static async Task<PiSharpExtensionExecResult> ExecuteOwnedAsync(string command,
        IReadOnlyList<string> args, string directory, TimeSpan? timeout, CancellationTokenSource operation)
    {
        using (operation)
            return await DirectProcessExecution.ExecuteAsync(command, args, directory, timeout, operation.Token)
                .ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task[] running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            running = _running.ToArray();
        }
        try
        {
            _lifetime.Cancel();
            try { Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
            catch (Exception) when (running.All(task => task.IsCompleted))
            {
                // Each caller observes its own execution failure; package cleanup must still finish.
            }
        }
        finally { _lifetime.Dispose(); }
    }
}
