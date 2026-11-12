using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

[Collection("Editor mutation PTY")]
public sealed class TreeFilterModePtyTests
{
    [Fact]
    public async Task ConfiguredFilterOpensTheTreeAndSettingsChangesPersistThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-tree-filter-mode-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        var sessionPath = Path.Combine(root, "tree.jsonl");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(sessionPath, SessionJsonl(root));
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["quietStartup"] = true,
                    ["shellCommandPrefix"] = "preserved",
                    ["treeFilterMode"] = "user-only"
                }));
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key",
                  "models":[{"id":"fixture-model","api":"openai-responses"}]}}}
                """);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            process = Start(root, agent, sessionPath);
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task<string> WaitForAsync(Func<string, bool> predicate, string description)
            {
                try { return await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{screen.LastFrame}");
                }
            }

            await WaitForAsync(frame => frame.Contains("ASSISTANT_2", StringComparison.Ordinal), "session transcript");

            await SendAsync("/tree\r");
            var configured = await WaitForAsync(frame => frame.Contains("[user]", StringComparison.Ordinal),
                "configured user-only tree filter");
            Assert.Contains("(2/2) [user]", configured);
            Assert.Equal(["user: USER_1", "user: USER_2"], TreeRows(configured));
            await SendAsync("\u001b");
            await WaitForAsync(frame => !frame.Contains("Session Tree", StringComparison.Ordinal), "tree closed");

            await SendAsync("/settings\r");
            await WaitForAsync(frame => frame.Contains("User settings", StringComparison.Ordinal), "settings list");
            await SendAsync("tree filter");
            var row = await WaitForAsync(frame => frame.Contains("Tree filter mode", StringComparison.Ordinal),
                "tree filter mode row");
            Assert.Contains("user-only", row);
            Assert.Contains("Default filter when opening /tree", row);
            await SendAsync("\r");
            var values = await WaitForAsync(frame => frame.Contains("Tree filter mode · user-only",
                StringComparison.Ordinal), "tree filter mode values");
            Assert.Contains("no-tools", values);
            Assert.Contains("labeled-only", values);
            await SendAsync("\u001b[A");
            await SendAsync("\r");
            await WaitForAsync(frame => frame.Contains("Saved user setting treeFilterMode = no-tools.",
                StringComparison.Ordinal), "settings save");
            await SendAsync("\u001b\u001b");
            await WaitForAsync(frame => !frame.Contains("Tree filter mode", StringComparison.Ordinal) &&
                frame.Contains("fixture-model", StringComparison.Ordinal), "settings closed");

            await SendAsync("/tree\r");
            var updated = await WaitForAsync(frame => frame.Contains("[no-tools]", StringComparison.Ordinal),
                "updated tree filter applies to the next open");
            Assert.Contains("(4/4) [no-tools]", updated);
            Assert.Equal(["user: USER_1", "assistant: ASSISTANT_1", "user: USER_2", "assistant: ASSISTANT_2"],
                TreeRows(updated));
            await SendAsync("\u001b");
            await WaitForAsync(frame => !frame.Contains("Session Tree", StringComparison.Ordinal), "tree closed again");

            await SendAsync("\u0015/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var captured = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Saved user setting treeFilterMode = no-tools.", captured);
            Assert.DoesNotContain("Agent error", await stderr);

            using (var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(agent, "settings.json"))))
            {
                Assert.Equal("no-tools", persisted.RootElement.GetProperty("treeFilterMode").GetString());
                Assert.Equal("preserved", persisted.RootElement.GetProperty("shellCommandPrefix").GetString());
                Assert.True(persisted.RootElement.GetProperty("quietStartup").GetBoolean());
            }

            process.Dispose();
            process = Start(root, agent, sessionPath);
            var restarted = new PtyScreenCapture(process, rows: 24, columns: 100);
            var restartedOutput = restarted.ReadCompletion;
            await restarted.InputReady.WaitAsync(timeout.Token);
            await restarted.WaitForFrameAsync(frame => frame.Contains("ASSISTANT_2", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/tree\r");
            await process.StandardInput.FlushAsync();
            var resumed = await restarted.WaitForFrameAsync(frame => frame.Contains("[no-tools]", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            Assert.Contains("(4/4) [no-tools]", resumed);
            await process.StandardInput.WriteAsync("\u001b\u0015/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await restartedOutput;
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DoubleEscapeOpensTheTreeWithTheConfiguredFilterThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-tree-filter-escape-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        var sessionPath = Path.Combine(root, "tree.jsonl");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(sessionPath, SessionJsonl(root));
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["quietStartup"] = true,
                    ["treeFilterMode"] = "labeled-only"
                }));
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key",
                  "models":[{"id":"fixture-model","api":"openai-responses"}]}}}
                """);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            process = Start(root, agent, sessionPath);
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            await screen.InputReady.WaitAsync(timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Contains("ASSISTANT_2", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);

            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.WriteAsync("\u001b");
            await process.StandardInput.FlushAsync();
            var opened = await screen.WaitForFrameAsync(frame => frame.Contains("Session Tree", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);
            Assert.Contains("[labeled]", opened);
            Assert.Contains("(1/1) [labeled]", opened);

            await process.StandardInput.WriteAsync("\u001b\u0015/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("never", "[user]", "(2/2) [user]")]
    [InlineData("always", "Session Tree", "(4/4)")]
    public async Task ProjectTrustDecidesWhetherAMalformedProjectFilterOverridesTheUserValueThroughLinuxPty(
        string defaultTrust, string expected, string expectedCount)
    {
        // Pinned Pi merges by presence: a trusted malformed project entry replaces the user value and
        // resolves to the default, while an untrusted project contributes nothing at all.
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-tree-filter-project-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        var sessionPath = Path.Combine(root, "tree.jsonl");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(sessionPath, SessionJsonl(root));
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"),
                "{\"treeFilterMode\":42}");
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["quietStartup"] = true,
                    ["defaultProjectTrust"] = defaultTrust,
                    ["treeFilterMode"] = "user-only"
                }));
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key",
                  "models":[{"id":"fixture-model","api":"openai-responses"}]}}}
                """);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            process = Start(root, agent, sessionPath);
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            await screen.InputReady.WaitAsync(timeout.Token);
            await screen.WaitForFrameAsync(frame => frame.Contains("ASSISTANT_2", StringComparison.Ordinal))
                .WaitAsync(timeout.Token);

            await process.StandardInput.WriteAsync("/tree\r");
            await process.StandardInput.FlushAsync();
            var opened = await screen.WaitForFrameAsync(frame =>
                frame.Contains("Session Tree", StringComparison.Ordinal) &&
                frame.Contains(expectedCount, StringComparison.Ordinal)).WaitAsync(timeout.Token);
            Assert.Contains(expected, opened);
            if (defaultTrust == "always")
                Assert.DoesNotContain("[user]", opened);

            await process.StandardInput.WriteAsync("\u001b\u0015/quit\r");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static Process Start(string root, string agent, string sessionPath)
    {
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(
            $"stty rows 24 cols 100; exec dotnet '{typeof(CliArguments).Assembly.Location}' " +
            $"--offline --no-tools --session '{sessionPath}'");
        start.ArgumentList.Add("/dev/null");
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        return Process.Start(start)!;
    }

    private static string[] TreeRows(string frame)
    {
        var rows = new List<string>();
        foreach (var line in frame.Split('\n'))
        {
            foreach (var marker in new[] { "user: USER_", "assistant: ASSISTANT_" })
            {
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) continue;
                rows.Add(line[index..].TrimEnd(' ', '|').Trim());
                break;
            }
        }
        return rows.ToArray();
    }

    private static string SessionJsonl(string workingDirectory)
    {
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                type = "session",
                version = 3,
                id = "tree-filter-mode",
                timestamp = "2025-01-01T00:00:00Z",
                cwd = workingDirectory
            })
        };
        foreach (var turn in new[] { 1, 2 })
        {
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "message",
                id = $"user-{turn}",
                parentId = turn == 1 ? null : $"assistant-{turn - 1}",
                timestamp = "2025-01-01T00:00:01Z",
                message = new
                {
                    role = "user",
                    content = new[] { new { type = "text", text = $"USER_{turn}" } },
                    timestamp = 1000
                }
            }));
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "message",
                id = $"assistant-{turn}",
                parentId = $"user-{turn}",
                timestamp = "2025-01-01T00:00:02Z",
                message = new
                {
                    role = "assistant",
                    content = new[] { new { type = "text", text = $"ASSISTANT_{turn}" } },
                    api = "openai-responses",
                    provider = "fixture",
                    model = "fixture-model",
                    usage = new
                    {
                        input = 1,
                        output = 1,
                        cacheRead = 0,
                        cacheWrite = 0,
                        totalTokens = 2,
                        cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 }
                    },
                    stopReason = "stop",
                    timestamp = 2000
                }
            }));
        }
        lines.Add(JsonSerializer.Serialize(new
        {
            type = "label",
            id = "label-1",
            parentId = "assistant-2",
            timestamp = "2025-01-01T00:00:03Z",
            targetId = "assistant-2",
            label = "answer label"
        }));
        return string.Join('\n', lines) + "\n";
    }
}
