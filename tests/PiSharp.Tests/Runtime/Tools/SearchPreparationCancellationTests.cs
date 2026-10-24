using Microsoft.Extensions.AI;
using PiSharp.Runtime;

namespace PiSharp.Tests;

[Collection("Extension environment")]
public sealed class SearchPreparationCancellationTests
{
    [Theory]
    [InlineData("grep")]
    [InlineData("find")]
    public async Task CancellationDuringPrivateExclusionPreparationKeepsTheToolAbortContract(string tool)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = SearchProcessProtocolTests.SourceFixture(tool, tool == "grep" ? "protocol" : "cancellation");
        var expected = tool == "grep" ? source.RootElement.GetProperty("realRipgrepCancellation").GetProperty("outcome").GetProperty("error").GetString()
            : source.RootElement.GetProperty("outcome").GetProperty("error").GetString();
        var root = Path.Combine(Path.GetTempPath(), "pisharp-search-preparation-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        var ready = Path.Combine(root, "ready");
        var pidFile = Path.Combine(root, "worker.pid");
        var inherited = Environment.GetEnvironmentVariable("PATH");
        using var cancellation = new CancellationTokenSource();
        Task<object?>? executing = null;
        try
        {
            var executable = Path.Combine(bin, "git");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\n" +
                $"printf '%s\\n' \"$$\" > {ProcessTestHelpers.ShellQuote(pidFile)}\ntouch {ProcessTestHelpers.ShellQuote(ready)}\nexec sleep 60\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + inherited);
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(function => function.Name == tool);
            executing = function.InvokeAsync(new AIFunctionArguments { ["pattern"] = "needle" }, cancellation.Token).AsTask();
            await ProcessTestHelpers.WaitForFileAsync(ready);
            var pid = ProcessTestHelpers.ReadLinuxProcessId(pidFile);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(expected, error.Message);
            ProcessTestHelpers.WaitForLinuxProcessExit(pid);
        }
        finally
        {
            cancellation.Cancel();
            Environment.SetEnvironmentVariable("PATH", inherited);
            if (executing is not null)
                try { await executing.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            Directory.Delete(root, recursive: true);
        }
    }
}
