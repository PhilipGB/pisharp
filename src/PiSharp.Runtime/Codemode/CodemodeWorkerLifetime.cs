using System.Diagnostics;

namespace PiSharp.Runtime.Codemode;

/// <summary>Keeps shared worker capacity until process exit and owns detached cleanup.</summary>
internal sealed class CodemodeWorkerLifetime
{
    internal const string AdmissionError = "Codemode worker limit reached (2 workers are still running or shutting down). Retry after they exit.";
    private static readonly SemaphoreSlim s_capacity = new(2, 2);
    internal static int ActiveWorkers => 2 - s_capacity.CurrentCount;

    internal static bool TryAcquire(out CodemodeWorkerLifetime? lifetime)
    {
        lifetime = s_capacity.Wait(0) ? new CodemodeWorkerLifetime() : null;
        return lifetime is not null;
    }

    private int _released;
    private Task _exit = Task.CompletedTask;

    internal void TrackExit(Task exit)
    {
        _exit = exit;
        _ = exit.ContinueWith(completed =>
        {
            if (completed.IsCompletedSuccessfully) Release();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) s_capacity.Release();
    }

    internal void Reap(Process process, CancellationTokenSource deadline, SemaphoreSlim writeGate,
        Task<string> stderr, IReadOnlyList<Task> outstanding)
    {
        // Request termination before settling, as Pi does; disposal can wait on the cleanup pool.
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _ = Task.Run(async () =>
        {
            try
            {
                await _exit;
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally
            {
                try { await Task.WhenAll(outstanding.Append(stderr)).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { }
                writeGate.Dispose();
                deadline.Dispose();
                process.Dispose();
            }
        });
    }
}
