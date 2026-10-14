using System.Diagnostics;
using System.Text.Json;
using PiSharp.Runtime.Codemode;

ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
if (!ThreadPool.SetMinThreads(1, minIo) || !ThreadPool.SetMaxThreads(1, maxIo))
    throw new InvalidOperationException("Cannot establish controlled worker scheduling.");
using var entered = new ManualResetEventSlim();
using var release = new ManualResetEventSlim();
var blockedPool = Task.Run(() => { entered.Set(); release.Wait(); });
if (!entered.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Worker pool barrier was not entered.");
using var process = new Process
{
    StartInfo = new ProcessStartInfo("/bin/sh")
    {
        ArgumentList = { "-c", "printf 'ready\\n'; exec sleep 60" },
        RedirectStandardOutput = true
    }
};
bool stopped;
try
{
    process.Start();
    if (process.StandardOutput.ReadLine() != "ready") throw new InvalidOperationException("Child was not ready.");
    if (!CodemodeWorkerLifetime.TryAcquire(out var lifetime)) throw new InvalidOperationException("Capacity was not free.");
    lifetime!.TrackExit(process.WaitForExitAsync());
    lifetime.Reap(process, new CancellationTokenSource(), new SemaphoreSlim(1), Task.FromResult(""), []);
    stopped = process.WaitForExit(1000);
}
finally
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    process.WaitForExit();
    release.Set();
    ThreadPool.SetMaxThreads(maxWorkers, maxIo);
    ThreadPool.SetMinThreads(minWorkers, minIo);
    blockedPool.GetAwaiter().GetResult();
}
Console.WriteLine(JsonSerializer.Serialize(new { terminationRequestedBeforePoolAvailable = stopped }));
