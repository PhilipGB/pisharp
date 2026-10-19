using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Extensions;

/// <summary>Owns direct executions for one loaded package without retaining package code.</summary>
internal sealed class NativeExtensionProcessExecutor(string workingDirectory, TimeSpan? unloadTimeout = null)
    : PiSharpExtensionProcessExecutor, IDisposable
{
    private readonly string _workingDirectory = Path.GetFullPath(workingDirectory);
    private readonly TimeSpan _unloadTimeout = unloadTimeout ?? TimeSpan.FromSeconds(15);
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
                // Package code may abandon the returned task when its load context is unloaded.
                _ = completed.Exception;
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
        var completion = Task.WhenAll(running);
        try
        {
            _lifetime.Cancel();
            completion.WaitAsync(_unloadTimeout).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Cancellation, cleanup failure, or the unload deadline must not interrupt catalog changes.
            // Pending executions keep their process ownership and finish termination and pipe cleanup.
        }
        finally
        {
            _ = completion.ContinueWith(completed =>
            {
                _ = completed.Exception;
                _lifetime.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
