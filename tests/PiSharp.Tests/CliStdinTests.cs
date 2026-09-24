using System.Diagnostics;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class CliStdinTests
{
    [Fact]
    public async Task RedirectedInputIsTrimmedAndBoundedBeforePromptComposition()
    {
        Assert.Equal("first\nsecond", await CliStdin.ReadAsync(new StringReader("  first\nsecond  ")));
        Assert.Equal(new string('x', CliStdin.MaximumCharacters),
            await CliStdin.ReadAsync(new StringReader(new string('x', CliStdin.MaximumCharacters))));
        await Assert.ThrowsAsync<InvalidDataException>(() => CliStdin.ReadAsync(
            new StringReader(new string(' ', CliStdin.MaximumCharacters + 1))));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CliStdin.ReadAsync(new StringReader("text"), cancelled.Token));
    }

    [Fact]
    public async Task OversizedPipedInputFailsWithoutProviderOrSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-stdin-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[] { typeof(CliArguments).Assembly.Location, "--provider", "openai", "--no-session", "--print", "hello" })
                start.ArgumentList.Add(arg);
            start.Environment["PISHARP_AGENT_DIR"] = Path.Combine(root, "agent");
            start.Environment.Remove("OPENAI_API_KEY");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(new string('x', CliStdin.MaximumCharacters + 1));
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, process.ExitCode);
            Assert.Equal("", await stdout);
            Assert.Contains("Redirected stdin exceeds", await stderr);
            Assert.DoesNotContain("not authenticated", await stderr);
        }
        finally { Directory.Delete(root, true); }
    }
}
