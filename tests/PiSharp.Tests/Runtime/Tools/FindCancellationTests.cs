using System.Diagnostics;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class FindCancellationTests
{
    [Fact]
    public async Task RealFdTraversalCancelsAtFirstResultTerminatesAndRecovers()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = SearchProcessProtocolTests.SourceFixture("find", "cancellation");
        var expected = source.RootElement;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-find-cancel-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, new string('x', expected.GetProperty("setup").GetProperty("directoryNameLength").GetInt32()));
        Directory.CreateDirectory(directory);
        using var cancellation = new CancellationTokenSource();
        IReadOnlyList<int> children = [];
        var aliveAtFirstResult = false;
        var observed = 0;
        try
        {
            var count = expected.GetProperty("setup").GetProperty("files").GetInt32();
            for (var index = 0; index < count; index++)
                await File.WriteAllTextAsync(Path.Combine(directory, index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".txt"), "");
            await File.WriteAllTextAsync(Path.Combine(root, "recovery.txt"), "unchanged");
            var execution = FdSearch.ExecuteAsync(root, "*", null, 0, cancellation.Token, () =>
            {
                if (Interlocked.Exchange(ref observed, 1) != 0) return;
                children = ProcessTestHelpers.FindChildrenWithArgument(root);
                aliveAtFirstResult = children.Any(child =>
                {
                    try { using var process = Process.GetProcessById(child); return !process.HasExited; }
                    catch (ArgumentException) { return false; }
                });
                cancellation.Cancel();
            });
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(expected.GetProperty("outcome").GetProperty("error").GetString(), error.Message);
            Assert.Equal(1, observed);
            Assert.True(aliveAtFirstResult);
            Assert.NotEmpty(children);
            foreach (var child in children) ProcessTestHelpers.WaitForLinuxProcessExit(child);
            Assert.Equal("gone", expected.GetProperty("stateAfterClose").GetString());
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(tool => tool.Name == "find");
            var continuation = await function.InvokeAsync(new AIFunctionArguments { ["pattern"] = "recovery.txt", ["limit"] = 1 });
            Assert.True(ToolResultOutput.TryRead(continuation, out var text, out _));
            Assert.Equal(expected.GetProperty("continuation").GetProperty("content")[0].GetProperty("text").GetString(), text);
            Assert.Equal(expected.GetProperty("inputFilesAfter").GetInt32(), Directory.EnumerateFiles(directory).Count());
            Assert.Equal(expected.GetProperty("recoveryFileAfter").GetString(), await File.ReadAllTextAsync(Path.Combine(root, "recovery.txt")));
        }
        finally
        {
            cancellation.Cancel();
            foreach (var child in ProcessTestHelpers.FindChildrenWithArgument(root))
            {
                try { using var process = Process.GetProcessById(child); process.Kill(entireProcessTree: true); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Directory.Delete(root, recursive: true);
        }
    }
}
