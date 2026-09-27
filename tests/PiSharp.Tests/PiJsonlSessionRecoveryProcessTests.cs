using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class PiJsonlSessionRecoveryProcessTests
{
    [Fact]
    public async Task RpcSessionKeepsValidDescendantAfterMalformedParentRow()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-jsonl-recovery-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var sourcePath = Path.Combine(root, "damaged-session.jsonl");
        var records = new[]
        {
            JsonSerializer.Serialize(new
            {
                type = "session", version = 3, id = "session-orphan",
                timestamp = "2026-09-27T10:00:00.000Z", cwd = root
            }),
            JsonSerializer.Serialize(new
            {
                type = "message", id = "entry-user", parentId = (string?)null,
                timestamp = "2026-09-27T10:00:01.000Z",
                message = new { role = "user", content = "before corrupt row", timestamp = 1780000000000L }
            }),
            JsonSerializer.Serialize(new
            {
                type = "message", id = "entry-assistant", parentId = "entry-missing",
                timestamp = "2026-09-27T10:00:03.000Z",
                message = new
                {
                    role = "assistant", content = "survives as orphan", timestamp = 1780000002000L,
                    provider = "openai", model = "gpt-4o"
                }
            })
        };
        var source = string.Join('\n', records[0], records[1], "not-json", records[2]) + "\n" +
            "{\"type\":\"message\",\"id\":\"interrupted-tail\"";
        await File.WriteAllTextAsync(sourcePath, source);
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
            foreach (var argument in new[] { "--mode", "rpc", "--offline", "--session", sourcePath,
                "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"read-1\",\"type\":\"get_messages\"}");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var errorText = await error.WaitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"Exit code: {process.ExitCode}\n{errorText}");
            Assert.Equal("", errorText);
            using var response = JsonDocument.Parse(Assert.Single(
                (await output.WaitAsync(timeout.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            var rootElement = response.RootElement;
            Assert.Equal("read-1", rootElement.GetProperty("id").GetString());
            Assert.True(rootElement.GetProperty("success").GetBoolean());
            var message = Assert.Single(rootElement.GetProperty("data").GetProperty("messages").EnumerateArray());
            Assert.Equal("assistant", message.GetProperty("role").GetString());
            Assert.Equal("survives as orphan", message.GetProperty("content").GetString());
            Assert.Equal(source, await File.ReadAllTextAsync(sourcePath));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RpcTreeResolvesAParentThatAppearsLaterInTheSessionFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-jsonl-forward-parent-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var sessionDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(agentDirectory);
        var sourcePath = Path.Combine(root, "forward-session.jsonl");
        var records = new[]
        {
            JsonSerializer.Serialize(new
            {
                type = "session", version = 3, id = "session-forward",
                timestamp = "2026-09-27T10:00:00.000Z", cwd = root
            }),
            JsonSerializer.Serialize(new
            {
                type = "message", id = "child-first", parentId = "parent-later",
                timestamp = "2026-09-27T10:00:01.000Z",
                message = new { role = "assistant", content = "child" }
            }),
            JsonSerializer.Serialize(new
            {
                type = "message", id = "parent-later", parentId = (string?)null,
                timestamp = "2026-09-27T10:00:02.000Z",
                message = new { role = "user", content = "parent" }
            })
        };
        var source = string.Join('\n', records) + "\n";
        await File.WriteAllTextAsync(sourcePath, source);
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
            foreach (var argument in new[] { "--mode", "rpc", "--offline", "--session", sourcePath,
                "--session-dir", sessionDirectory })
                start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"tree-1\",\"type\":\"get_tree\"}");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var errorText = await error.WaitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"Exit code: {process.ExitCode}\n{errorText}");
            Assert.Equal("", errorText);
            using var response = JsonDocument.Parse(Assert.Single(
                (await output.WaitAsync(timeout.Token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
            Assert.Equal("tree-1", response.RootElement.GetProperty("id").GetString());
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            var roots = response.RootElement.GetProperty("data").GetProperty("tree").EnumerateArray();
            var parent = Assert.Single(roots, node => node.GetProperty("entry").GetProperty("id").GetString() == "parent-later");
            Assert.Contains(parent.GetProperty("children").EnumerateArray(),
                node => node.GetProperty("entry").GetProperty("id").GetString() == "child-first");
            Assert.Equal(source, await File.ReadAllTextAsync(sourcePath));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
