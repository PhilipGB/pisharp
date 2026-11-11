using System.Diagnostics;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

[Collection("Editor mutation PTY")]
public sealed class OpenCodeApiKeyLoginPtyTests
{
    [Fact]
    public async Task InteractiveApiKeyLoginIsMaskedEditableCancellableAndLeavesTheTuiUsable()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = NewRoot();
        var agent = Path.Combine(root, "agent");
        var secret = "sk-abc";
        Process? process = null;
        try
        {
            Directory.CreateDirectory(agent);
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"name":"Fixture","baseUrl":"http://127.0.0.1:1/v1",
                 "apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","reasoning":false}]}}}
                """);
            var started = Start(root, agent,
                "--provider", "fixture", "--model", "fixture-model", "--no-tools", "--offline", "--no-session");
            process = started.Process;
            var capture = started.Capture;
            var stderr = started.Stderr;
            var completion = started.Completion;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));

            await capture.WaitForFrameAsync(frame => frame.Contains("fixture-model")).WaitAsync(deadline.Token);
            await SendAsync(process, "/login\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Select authentication method"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "\u001b[B\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Select API key provider"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "fixture");
            await capture.WaitForFrameAsync(frame => frame.Contains("Search: fixture")).WaitAsync(deadline.Token);
            await SendAsync(process, "\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("API key for fixture:") &&
                frame.Contains("Login to Fixture")).WaitAsync(deadline.Token);

            await SendAsync(process, secret);
            await capture.WaitForFrameAsync(frame => frame.Contains("\u2022\u2022\u2022\u2022\u2022\u2022") &&
                !frame.Contains(secret)).WaitAsync(deadline.Token);
            await SendAsync(process, "\u007f");
            await capture.WaitForFrameAsync(frame => frame.Contains("\u2022\u2022\u2022\u2022\u2022") &&
                !frame.Contains("\u2022\u2022\u2022\u2022\u2022\u2022")).WaitAsync(deadline.Token);
            await SendAsync(process, "d");
            await capture.WaitForFrameAsync(frame => frame.Contains("\u2022\u2022\u2022\u2022\u2022\u2022"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "\u001b");
            await capture.WaitForFrameAsync(frame => frame.Contains("Login cancelled.")).WaitAsync(deadline.Token);
            await Task.Delay(300, deadline.Token);
            await SendAsync(process, "\u001b");
            await Task.Delay(300, deadline.Token);
            await SendAsync(process, "\u001b");
            await Task.Delay(700, deadline.Token);
            Assert.DoesNotContain(secret, capture.CapturedOutput, StringComparison.Ordinal);
            Assert.Null(await new AuthStorage(Path.Combine(agent, "auth.json")).ReadAsync("fixture"));

            await SendAsync(process, "/model\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Only showing models from configured providers"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "\u001b");
            await capture.WaitForFrameAsync(frame => !frame.Contains("Only showing models from configured providers"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var transcript = await completion;
            var errors = await stderr;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain(secret, transcript, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, errors, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception:", errors, StringComparison.Ordinal);
        }
        catch
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            throw;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OpenCodeGoLoginUsesTheTuiSelectsModelsLogsOutAndPersistsAcrossRestart()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = NewRoot();
        var agent = Path.Combine(root, "agent");
        var firstSecret = "go-secret-123";
        var secondSecret = "go-secret-456";
        Process? process = null;
        try
        {
            Directory.CreateDirectory(agent);
            var started = Start(root, agent,
                "--provider", "opencode-go", "--model", "kimi-k3", "--no-tools", "--offline", "--no-session");
            process = started.Process;
            var capture = started.Capture;
            var stderr = started.Stderr;
            var completion = started.Completion;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await capture.WaitForFrameAsync(frame => frame.Contains("kimi-k3")).WaitAsync(deadline.Token);
            await SendAsync(process, "/login\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Select authentication method"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "\u001b[B\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Select API key provider"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "opencode go");
            await capture.WaitForFrameAsync(frame => frame.Contains("Search: opencode go"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("API key for opencode-go:") &&
                frame.Contains("Login to OpenCode Go")).WaitAsync(deadline.Token);
            await SendAsync(process, firstSecret);
            await SendAsync(process, "\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Authenticated opencode-go with api-key"))
                .WaitAsync(deadline.Token);
            await Task.Delay(500, deadline.Token);
            Assert.DoesNotContain(firstSecret, capture.CapturedOutput, StringComparison.Ordinal);

            await SendAsync(process, "/model opencode-go/claude-haiku-5-5\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("claude-haiku-5-5")).WaitAsync(deadline.Token);
            await Task.Delay(1500, deadline.Token);

            await SendAsync(process, "/model\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Only showing models from configured providers"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, "claude-haiku-5-5");
            await capture.WaitForFrameAsync(frame => frame.Contains("claude-haiku-5-5") &&
                frame.Contains("opencode-go")).WaitAsync(deadline.Token);
            await SendAsync(process, "\u001b");
            await capture.WaitForFrameAsync(frame => !frame.Contains("Only showing models from configured providers"))
                .WaitAsync(deadline.Token);
            await Task.Delay(500, deadline.Token);

            await SendAsync(process, "/logout opencode-go\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Logged out opencode-go."))
                .WaitAsync(deadline.Token);
            await Task.Delay(500, deadline.Token);
            Assert.Null(await new AuthStorage(Path.Combine(agent, "auth.json")).ReadAsync("opencode-go"));

            await SendAsync(process, "\u001b[200~/login opencode-go api-key\u001b[201~\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("API key for opencode-go:"))
                .WaitAsync(deadline.Token);
            await SendAsync(process, secondSecret);
            await SendAsync(process, "\r");
            await capture.WaitForFrameAsync(frame => frame.Contains("Authenticated opencode-go with api-key"))
                .WaitAsync(deadline.Token);
            await Task.Delay(500, deadline.Token);
            await SendAsync(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var transcript = await completion;
            var errors = await stderr;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain(firstSecret, transcript, StringComparison.Ordinal);
            Assert.DoesNotContain(secondSecret, transcript, StringComparison.Ordinal);
            Assert.DoesNotContain(firstSecret, errors, StringComparison.Ordinal);
            Assert.DoesNotContain(secondSecret, errors, StringComparison.Ordinal);

            var stored = await new AuthStorage(Path.Combine(agent, "auth.json")).ReadAsync("opencode-go");
            Assert.Equal(secondSecret, stored?.Key);

            var check = await RunAuthCheckAsync(root, agent);
            Assert.Equal(0, check.ExitCode);
            Assert.Contains("credential available", check.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(secondSecret, check.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(secondSecret, check.Error, StringComparison.Ordinal);
        }
        catch
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            throw;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static (Process Process, PtyScreenCapture Capture, Task<string> Completion, Task<string> Stderr) Start(
        string root, string agent, params string[] arguments)
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
        var command = new List<string> { "stty rows 24 cols 100; exec dotnet", ShellQuote(typeof(CliArguments).Assembly.Location) };
        command.AddRange(arguments.Select(ShellQuote));
        start.ArgumentList.Add(string.Join(' ', command));
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENCODE_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "PISHARP_API_KEY",
                     "PISHARP_FIXTURE_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH",
                     "PISHARP_SETTINGS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the PTY process.");
        var capture = new PtyScreenCapture(process, rows: 24, columns: 100);
        var completion = capture.ReadCompletion;
        return (process, capture, completion, process.StandardError.ReadToEndAsync());
    }

    private static async Task SendAsync(Process process, string text)
    {
        await process.StandardInput.WriteAsync(text);
        await process.StandardInput.FlushAsync();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAuthCheckAsync(string root, string agent)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        start.ArgumentList.Add("auth");
        start.ArgumentList.Add("check");
        start.ArgumentList.Add("--provider");
        start.ArgumentList.Add("opencode-go");
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        start.Environment.Remove("OPENCODE_API_KEY");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start auth check.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-opencode-login-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
