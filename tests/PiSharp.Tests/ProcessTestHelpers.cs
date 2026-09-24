namespace PiSharp.Tests;

internal static class ProcessTestHelpers
{
    public static async Task WaitForFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path)) return;
        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, name)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        FileSystemEventHandler found = (_, _) => ready.TrySetResult();
        RenamedEventHandler renamed = (_, _) => ready.TrySetResult();
        watcher.Created += found;
        watcher.Changed += found;
        watcher.Renamed += renamed;
        if (File.Exists(path)) ready.TrySetResult();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    public static int ReadLinuxProcessId(string path) => int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture);

    public static void WaitForLinuxProcessExit(int processId)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var path = $"/proc/{processId}/stat";
            if (!File.Exists(path)) return true;
            var stat = File.ReadAllText(path);
            var close = stat.LastIndexOf(')');
            return close >= 0 && stat.AsSpan(close + 2).StartsWith("Z ", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5)), $"Process {processId} did not exit.");
    }

    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

}
