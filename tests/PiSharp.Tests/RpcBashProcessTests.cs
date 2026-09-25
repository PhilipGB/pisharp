using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcBashProcessTests
{
    [Fact]
    public async Task RpcProcessReturnsBashResultsAndHandlesAbortBash()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-bash-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        var started = Path.Combine(root, "started");
        var released = Path.Combine(root, "released");
        var finished = Path.Combine(root, "finished");
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--local", "--offline", "--no-session" })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var stderr = process.StandardError.ReadToEndAsync();
            var firstLines = new List<string>();
            await WriteCommandAsync(process, new
            {
                id = "process-bash",
                type = "bash",
                command = "printf process-stdout; printf process-stderr >&2; exit 7"
            }, timeout.Token);
            await ReadResponsesAsync(process, ["process-bash"], firstLines, timeout.Token);

            using (var response = JsonDocument.Parse(Assert.Single(firstLines, line =>
                line.Contains("\"id\":\"process-bash\"", StringComparison.Ordinal) && line.Contains("\"command\":\"bash\"", StringComparison.Ordinal))))
            {
                var data = response.RootElement.GetProperty("data");
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(7, data.GetProperty("exitCode").GetInt32());
                Assert.Contains("process-stdout", data.GetProperty("output").GetString());
                Assert.Contains("process-stderr", data.GetProperty("output").GetString());
            }
            var updates = firstLines.Where(line => line.Contains("bash_execution_update", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(updates);
            foreach (var line in updates)
            {
                using var update = JsonDocument.Parse(line);
                Assert.Equal("process-bash", update.RootElement.GetProperty("data").GetProperty("OperationId").GetString());
            }

            var command = $"touch {ProcessTestHelpers.ShellQuote(started)}; while [ ! -e {ProcessTestHelpers.ShellQuote(released)} ]; do :; done; touch {ProcessTestHelpers.ShellQuote(finished)}";
            await WriteCommandAsync(process, new { id = "process-aborted-bash", type = "bash", command }, timeout.Token);
            await ProcessTestHelpers.WaitForFileAsync(started, timeout.Token);
            await WriteCommandAsync(process, new { id = "process-abort", type = "abort_bash" }, timeout.Token);
            var cancellationLines = new List<string>();
            await ReadResponsesAsync(process, ["process-aborted-bash", "process-abort"], cancellationLines, timeout.Token);
            using (var response = JsonDocument.Parse(Assert.Single(cancellationLines, line =>
                line.Contains("\"id\":\"process-aborted-bash\"", StringComparison.Ordinal) && line.Contains("\"command\":\"bash\"", StringComparison.Ordinal))))
            {
                var data = response.RootElement.GetProperty("data");
                Assert.True(response.RootElement.GetProperty("success").GetBoolean());
                Assert.True(data.GetProperty("cancelled").GetBoolean());
                Assert.False(data.TryGetProperty("exitCode", out _));
            }
            Assert.False(File.Exists(finished));

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await stderr.WaitAsync(timeout.Token));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteCommandAsync(Process process, object command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task ReadResponsesAsync(Process process, IEnumerable<string> ids, List<string> lines,
        CancellationToken cancellationToken)
    {
        var remaining = ids.ToHashSet(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("RPC process closed stdout before returning all command responses.");
            lines.Add(line);
            using var record = JsonDocument.Parse(line);
            var root = record.RootElement;
            if (root.GetProperty("type").GetString() == "response" && root.TryGetProperty("id", out var id))
                remaining.Remove(id.GetString() ?? "");
        }
    }
}
