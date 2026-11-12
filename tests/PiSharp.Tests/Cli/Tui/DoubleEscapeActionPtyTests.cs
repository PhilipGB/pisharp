using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

[CollectionDefinition("Double-escape action PTY", DisableParallelization = true)]
public sealed class DoubleEscapeActionPtyCollection;

/// <summary>
/// End-to-end `doubleEscapeAction` contracts that the paired fixtures cannot express: the PiSharp
/// settings list is a value submenu rather than Pi's cycling row, and the malformed-value case needs
/// a product-specific startup assertion.
/// </summary>
[Collection("Double-escape action PTY")]
public sealed class DoubleEscapeActionPtyTests
{
    [Fact]
    public async Task SettingsChangeAppliesToTheNextPairAndSurvivesARestartThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("double-escape-settings");
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"quietStartup\":true}");
        Process? process = null;
        Process? restarted = null;
        try
        {
            process = Start(root, agent);
            var screen = new PtyScreenCapture(process, 24, 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(Process target, string input)
            {
                await target.StandardInput.WriteAsync(input);
                await target.StandardInput.FlushAsync();
            }

            async Task WaitForAsync(PtyScreenCapture target, Func<string, bool> predicate, string description)
            {
                try { await target.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{target.LastFrame}");
                }
            }

            await SendAsync(process, "/settings\rdouble-escape");
            await WaitForAsync(screen, frame => frame.Contains("Double-escape action", StringComparison.Ordinal) &&
                frame.Contains("Action when pressing Escape twice with empty editor", StringComparison.Ordinal) &&
                frame.Contains("inherit/default (default)", StringComparison.Ordinal),
                "settings row for an unset double-escape action");

            await SendAsync(process, "\r");
            var values = await screen.WaitForFrameAsync(frame =>
                frame.Contains("Double-escape action ·", StringComparison.Ordinal) &&
                frame.Contains("Inherit / default", StringComparison.Ordinal) &&
                frame.Contains("fork", StringComparison.Ordinal) &&
                frame.Contains("none", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            var options = new[] { "Inherit / default", "tree", "fork", "none" };
            var positions = options.Select(option => values.IndexOf(option, StringComparison.Ordinal)).ToArray();
            Assert.All(positions, position => Assert.True(position >= 0));
            Assert.Equal(positions.Order(), positions);

            // The cursor starts on the current value; forkl is two rows below it.
            await SendAsync(process, "\u001b[B\u001b[B\r");
            await WaitForAsync(screen, frame =>
                frame.Split('\n').Any(line => line.Contains("Double-escape action", StringComparison.Ordinal) &&
                    line.Contains("fork", StringComparison.Ordinal)) &&
                !frame.Contains("Double-escape action ·", StringComparison.Ordinal),
                "fork saved into the settings list");
            await SendAsync(process, "\u001b");
            await WaitForAsync(screen, frame => !frame.Contains("Double-escape action", StringComparison.Ordinal),
                "settings closed");

            // No restart: the next empty-editor pair must use the new value. This fixture runs
            // without a session, so the fork path reports its empty state.
            await SendAsync(process, "\u001b\u001b");
            await WaitForAsync(screen, frame =>
                frame.Contains("No user messages with text are available to fork.", StringComparison.Ordinal),
                "live setting change takes the fork path");
            await Task.Delay(200);

            await SendAsync(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Exception:", await stderr);

            var persisted = await UserSettings.LoadAsync(agent, _ => null);
            Assert.Equal("fork", persisted.DoubleEscapeAction?.Raw);
            Assert.Equal("fork", persisted.EffectiveDoubleEscapeAction);

            restarted = Start(root, agent);
            var restartedScreen = new PtyScreenCapture(restarted, 24, 100);
            var restartedOutput = restartedScreen.ReadCompletion;
            var restartedError = restarted.StandardError.ReadToEndAsync();
            await restartedScreen.InputReady.WaitAsync(timeout.Token);
            await SendAsync(restarted, "\u001b\u001b");
            await WaitForAsync(restartedScreen, frame =>
                frame.Contains("No user messages with text are available to fork.", StringComparison.Ordinal),
                "persisted value applies after a restart");
            await SendAsync(restarted, "\u001b");
            await Task.Delay(200);
            await SendAsync(restarted, "/quit\r");
            restarted.StandardInput.Close();
            await restarted.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, restarted.ExitCode);
            Assert.DoesNotContain("Agent error", await restartedOutput);
            Assert.DoesNotContain("Exception:", await restartedError);
        }
        finally
        {
            Cleanup(restarted, root);
            Cleanup(process, null);
        }
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"bogus\"")]
    [InlineData("null")]
    public async Task MalformedStoredValuesLoadAndKeepThePinnedRuntimeBehaviourThroughLinuxPty(string storedValue)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = TemporaryDirectory("double-escape-malformed-" + Math.Abs(storedValue.GetHashCode() % 1000));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
            "{\"quietStartup\":true,\"doubleEscapeAction\":" + storedValue + "}");
        Process? process = null;
        try
        {
            process = Start(root, agent);
            var screen = new PtyScreenCapture(process, 24, 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await screen.InputReady.WaitAsync(timeout.Token);

            async Task SendAsync(string input)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            async Task WaitForAsync(Func<string, bool> predicate, string description)
            {
                try { await screen.WaitForFrameAsync(predicate).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"{description} timed out. Current frame:\n{screen.LastFrame}");
                }
            }

            // Startup must succeed: pinned Pi loads the value without validating it.
            await SendAsync("\u001b\u001b");
            // This fixture runs without a session, so the fork path reports its empty state instead
            // of rendering the message list; the tree path still opens the tree selector.
            var expected = storedValue == "null" ? "Session Tree"
                : "No user messages with text are available to fork.";
            await WaitForAsync(frame => frame.Contains(expected, StringComparison.Ordinal),
                $"{storedValue} opens the pinned selector");
            // A status line stays in the transcript, so the next assertion is the usable editor.
            // The pause keeps the following text from decoding as an Escape-prefixed key.
            await SendAsync("\u001b");
            await Task.Delay(200);
            await SendAsync("editor ready");
            await WaitForAsync(frame => frame.Contains("editor ready", StringComparison.Ordinal),
                "editor usable after a malformed value");
            await SendAsync("\u0015/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            var output = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Invalid settings", output);
            Assert.DoesNotContain("Agent error", output);
            Assert.DoesNotContain("Exception:", await stderr);
            Assert.DoesNotContain("must be", output);
        }
        finally { Cleanup(process, root); }
    }

    private static Process Start(string root, string agent)
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
        start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session --no-tools");
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL",
                     "PISHARP_MODEL", "PISHARP_SETTINGS_PATH", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the double-escape PTY fixture.");
    }

    private static string TemporaryDirectory(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pisharp-{name}-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(Process? process, string? root)
    {
        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        process?.Dispose();
        if (root is not null) Directory.Delete(root, recursive: true);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
