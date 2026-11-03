using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class KeysModelDisplayPtyTests
{
    private const string FixtureModels = """
        {"providers":{"fixture":{"baseUrl":"{baseUrl}","apiKeyEnv":"PISHARP_KEYS_KEY","models":[
          {"id":"fixture-model","name":"Fixture Model","api":"openai-responses","reasoning":true},
          {"id":"alpha","name":"Alpha Model","api":"openai-responses","reasoning":true},
          {"id":"beta","name":"Beta Model","api":"openai-responses","reasoning":true}
        ]}}}
        """;

    [Fact]
    public async Task ModelCycleSelectorAndContextPreservationMatchPinnedPiThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-model-display-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        using var listener = StartListener(out var port);
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var requests = new ConcurrentQueue<string>();
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
                using var reader = new StreamReader(context.Request.InputStream);
                requests.Enqueue(await reader.ReadToEndAsync(serverTimeout.Token));
                context.Response.ContentType = "text/event-stream";
                context.Response.KeepAlive = false;
                await using var writer = new StreamWriter(context.Response.OutputStream);
                await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_model_" +
                    index + "\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_model_" +
                    index + "\",\"output_index\":0,\"content_index\":0,\"delta\":\"reply " + (index + 1) + "\"}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_model_" +
                    index + "\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture\",\"status\":\"completed\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                context.Response.Close();
            }
        });
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
                "{\"defaultThinkingLevel\":\"medium\"}");
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"),
                FixtureModels.Replace("{baseUrl}", $"http://127.0.0.1:{port}/v1", StringComparison.Ordinal));
            process = Start(cwd: root, agent);
            var screen = new PtyScreenCapture(process, rows: 30, columns: 110);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await screen.InputReady.WaitAsync(timeout.Token);
            await WaitForFrameAsync(screen, frame => frame.Split('\n').Any(line =>
                line.Contains("fixture-model", StringComparison.Ordinal) &&
                line.Contains("medium", StringComparison.Ordinal)), timeout.Token);

            await SendAsync(process, "\u0010");
            await WaitForOutputAsync(screen, "Switched to Alpha Model (thinking: medium)", timeout.Token);
            await WaitForFrameAsync(screen, frame => frame.Split('\n').Any(line =>
                line.Contains("alpha", StringComparison.Ordinal) && line.Contains("medium", StringComparison.Ordinal)),
                timeout.Token);

            await SendAsync(process, "first prompt\r");
            var first = await WaitForRequestAsync(requests, 1, timeout.Token);
            using (var parsed = JsonDocument.Parse(first))
            {
                Assert.Equal("alpha", parsed.RootElement.GetProperty("model").GetString());
                Assert.Equal("medium", parsed.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.Contains("first prompt", first, StringComparison.Ordinal);
            }
            await WaitForFrameAsync(screen, frame => frame.Contains("reply 1", StringComparison.Ordinal), timeout.Token);

            await SendAsync(process, "\u000c");
            await WaitForFrameAsync(screen, frame => frame.Contains("Model Name:", StringComparison.Ordinal),
                timeout.Token);
            // The selector owns the widget while open: Ctrl+P must not cycle the model underneath.
            await SendAsync(process, "\u0010");
            await Task.Delay(100, timeout.Token);
            await WaitForFrameAsync(screen, frame => frame.Contains("Model Name:", StringComparison.Ordinal),
                timeout.Token);
            Assert.DoesNotContain("Switched to Beta Model", screen.CapturedOutput, StringComparison.Ordinal);
            await SendAsync(process, "beta");
            await WaitForFrameAsync(screen, frame => frame.Contains("> beta", StringComparison.Ordinal) &&
                frame.Contains("Model Name: Beta Model", StringComparison.Ordinal), timeout.Token);
            await SendAsync(process, "\r");
            await WaitForOutputAsync(screen, "Model: beta", timeout.Token);
            await WaitForFrameAsync(screen, frame => frame.Split('\n').Any(line =>
                line.Contains("beta", StringComparison.Ordinal) && line.Contains("medium", StringComparison.Ordinal)),
                timeout.Token);

            await SendAsync(process, "second prompt\r");
            var second = await WaitForRequestAsync(requests, 2, timeout.Token);
            using (var parsed = JsonDocument.Parse(second))
            {
                Assert.Equal("beta", parsed.RootElement.GetProperty("model").GetString());
                var firstUser = second.IndexOf("first prompt", StringComparison.Ordinal);
                var priorReply = second.IndexOf("reply 1", StringComparison.Ordinal);
                var secondUser = second.IndexOf("second prompt", StringComparison.Ordinal);
                Assert.True(firstUser >= 0 && priorReply > firstUser && secondUser > priorReply,
                    "The second request did not retain the first user message and assistant reply in order.");
            }
            await WaitForFrameAsync(screen, frame => frame.Contains("reply 2", StringComparison.Ordinal), timeout.Token);

            await SendAsync(process, "\u001b[112;6u");
            await WaitForOutputAsync(screen, "Switched to Alpha Model", timeout.Token);

            await SendAsync(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stdout);
            Assert.DoesNotContain("Unhandled exception", await stderr);
            Assert.Equal(2, requests.Count);
        }
        finally
        {
            serverTimeout.Cancel();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            try { await server; }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) { }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ThinkingVisibilityToolExpansionAndCopyFollowPinnedKeysThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-model-keys-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        var sessionPath = Path.Combine(root, "keys-session.session.json");
        var capture = Path.Combine(root, "clipboard.txt");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key",
              "models":[{"id":"fixture-model","api":"openai-responses","reasoning":true}]}}}
            """);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{}");
        var store = new PiSharp.Runtime.Sessions.ConversationStore(root, Path.Combine(root, "sessions"));
        var conversation = new PiSharp.Runtime.Sessions.ConversationSession(root,
            ConnectionSettings.LocalModel, new Uri(ConnectionSettings.LocalEndpoint).ToString());
        conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
            "assistant response for copy"));
        await store.SaveAsync(conversation, sessionPath);
        var wlCopy = Path.Combine(bin, "wl-copy");
        await File.WriteAllTextAsync(wlCopy, "#!/bin/sh\ncat > \"$PISHARP_CLIP_CAPTURE\"\n");
        File.SetUnixFileMode(wlCopy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process? process = null;
        Process? restarted = null;
        try
        {
            process = StartLocal(root, agent,
                $"--session {Quote(sessionPath)} --session-dir {Quote(store.DirectoryPath)}",
                new Dictionary<string, string>
                {
                    ["WAYLAND_DISPLAY"] = "wayland-0",
                    ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                    ["PISHARP_CLIP_CAPTURE"] = capture
                });
            var screen = new PtyScreenCapture(process, rows: 24, columns: 90);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await screen.InputReady.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new InvalidOperationException("PTY did not become ready. stderr:\n" + await stderr +
                    "\nstdout:\n" + new string((await stdout).TakeLast(4000).ToArray()));
            }

            await SendAsync(process, "\u0014");
            await WaitForOutputAsync(screen, "Thinking blocks: hidden", timeout.Token);
            var hidden = await UserSettings.LoadAsync(agent, _ => null);
            Assert.True(hidden.HideThinkingBlock);

            await SendAsync(process, "\u0014");
            await WaitForOutputAsync(screen, "Thinking blocks: visible", timeout.Token);
            var visible = await UserSettings.LoadAsync(agent, _ => null);
            Assert.False(visible.HideThinkingBlock);

            await SendAsync(process, "\u0014");
            await WaitForOutputAsync(screen, "Thinking blocks: hidden", timeout.Token);

            await SendAsync(process, "\u000f");
            await WaitForOutputAsync(screen, "Tool output: expanded", timeout.Token);
            await SendAsync(process, "\u000f");
            await WaitForOutputAsync(screen, "Tool output: collapsed", timeout.Token);

            await SendAsync(process, "\u0018");
            await WaitForOutputAsync(screen, "Copied!", timeout.Token);
            Assert.Equal("assistant response for copy", await File.ReadAllTextAsync(capture));

            await SendAsync(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            _ = await stdout;
            await stderr;

            var configured = await UserSettings.LoadAsync(agent, _ => null);
            Assert.True(configured.HideThinkingBlock);
            restarted = StartLocal(root, agent,
                $"--session {Quote(sessionPath)} --session-dir {Quote(store.DirectoryPath)}");
            var restartedScreen = new PtyScreenCapture(restarted, rows: 24, columns: 90);
            var restartedStdout = restartedScreen.ReadCompletion;
            var restartedStderr = restarted.StandardError.ReadToEndAsync();
            using var restartTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await restartedScreen.InputReady.WaitAsync(restartTimeout.Token);
            await SendAsync(restarted, "\u0014");
            await WaitForOutputAsync(restartedScreen, "Thinking blocks: visible", restartTimeout.Token);
            await SendAsync(restarted, "/quit\r");
            restarted.StandardInput.Close();
            await restarted.WaitForExitAsync(restartTimeout.Token);
            _ = await restartedStdout;
            Assert.DoesNotContain("Unhandled exception", await restartedStderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            if (restarted is { HasExited: false }) restarted.Kill(entireProcessTree: true);
            restarted?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ConfigurableModelThinkingToolAndCopyBindingsAndWidgetPrecedenceThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-model-rebind-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "keybindings.json"), """
            {
              "app.model.cycleForward": "f6",
              "app.model.cycleBackward": "f7",
              "app.thinking.cycle": "f8",
              "app.model.select": "f9",
              "app.tools.expand": "f10",
              "app.message.copy": "f11"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"defaultThinkingLevel\":\"medium\"}");
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[
              {"id":"fixture-model","name":"Fixture Model","api":"openai-responses","reasoning":true},
              {"id":"second-model","name":"Second Model","api":"openai-responses","reasoning":true}
            ]}}}
            """);
        var sessionPath = Path.Combine(root, "rebind-session.session.json");
        var store = new PiSharp.Runtime.Sessions.ConversationStore(root, Path.Combine(root, "sessions"));
        var conversation = new PiSharp.Runtime.Sessions.ConversationSession(root, "fixture-model",
            new Uri("http://127.0.0.1:1/v1").ToString());
        conversation.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "rebind copy"));
        await store.SaveAsync(conversation, sessionPath);
        Process? process = null;
        try
        {
            process = Start(root, agent, offline: true,
                $"--session {Quote(sessionPath)} --session-dir {Quote(store.DirectoryPath)}");
            var screen = new PtyScreenCapture(process, rows: 24, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            await screen.InputReady.WaitAsync(timeout.Token);
            await WaitForFrameAsync(screen, frame => frame.Split('\n').Any(line =>
                line.Contains("fixture-model", StringComparison.Ordinal) && line.Contains("medium", StringComparison.Ordinal)),
                timeout.Token);

            // A saved session starts from the cached snapshot; the selector refreshes the full
            // configured catalog so the cycle has more than the current model.
            await SendAsync(process, "\u001b[20~"); // F9
            await WaitForFrameAsync(screen, frame => frame.Contains("second-model [fixture]", StringComparison.Ordinal),
                timeout.Token);
            await SendAsync(process, "\u001b");
            await WaitForFrameAsync(screen, frame => !frame.Contains("Model Name:", StringComparison.Ordinal),
                timeout.Token);

            // Original bindings are inert.
            await SendAsync(process, "\u0010");
            await Task.Delay(100, timeout.Token);
            await SendAsync(process, "\u001b[Z");
            await Task.Delay(100, timeout.Token);
            Assert.DoesNotContain("Switched to Second Model", screen.CapturedOutput, StringComparison.Ordinal);

            await SendAsync(process, "\u001b[17~"); // F6
            await WaitForOutputAsync(screen, "Switched to Second Model (thinking: medium)", timeout.Token);
            await SendAsync(process, "\u001b[18~"); // F7
            await WaitForOutputAsync(screen, "Switched to Fixture Model (thinking: medium)", timeout.Token);
            await SendAsync(process, "\u001b[19~"); // F8
            await WaitForOutputAsync(screen, "Thinking level: high", timeout.Token);
            await SendAsync(process, "\u001b[20~"); // F9
            await WaitForFrameAsync(screen, frame => frame.Contains("Model Name:", StringComparison.Ordinal),
                timeout.Token);
            await SendAsync(process, "\u001b");
            await WaitForFrameAsync(screen, frame => !frame.Contains("Model Name:", StringComparison.Ordinal),
                timeout.Token);
            await SendAsync(process, "\u001b[21~"); // F10
            await WaitForOutputAsync(screen, "Tool output: expanded", timeout.Token);
            await SendAsync(process, "\u001b[23~"); // F11
            await WaitForOutputAsync(screen, "Copied!", timeout.Token);

            await SendAsync(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Unhandled exception", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, true);
        }
    }

    private static Process Start(string cwd, string agent, bool offline = true, string extraArguments = "",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("-c");
        var sessionArguments = extraArguments.Contains("--session ", StringComparison.Ordinal) ? " " : " --no-session";
        start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {Quote(typeof(CliArguments).Assembly.Location)} " +
            $"--provider fixture --model fixture-model{sessionArguments}{(offline ? " --offline" : "")} --no-tools " +
            extraArguments);
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        start.Environment["PISHARP_KEYS_KEY"] = "fixture-key";
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the interactive PTY fixture.");
    }

    private static Process StartLocal(string cwd, string agent, string extraArguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-q");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"stty rows 24 cols 100; exec dotnet {Quote(typeof(CliArguments).Assembly.Location)} " +
            $"--local --offline --no-tools {extraArguments}");
        start.ArgumentList.Add("/dev/null");
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        if (environment is not null)
            foreach (var (name, value) in environment) start.Environment[name] = value;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the local PTY fixture.");
    }

    private static HttpListener StartListener(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var candidate = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
                if (attempt == 9) throw;
            }
        }
        throw new InvalidOperationException("Could not reserve a loopback port.");
    }

    private static async Task SendAsync(Process process, string text)
    {
        await process.StandardInput.WriteAsync(text);
        await process.StandardInput.FlushAsync();
    }

    private static async Task WaitForOutputAsync(PtyScreenCapture screen, string expected, CancellationToken token)
    {
        try
        {
            while (!screen.CapturedOutput.Contains(expected, StringComparison.Ordinal))
                await Task.Delay(10, token);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"Timed out waiting for output '{expected}'. Last frame:\n" +
                screen.LastFrame + "\nOutput tail:\n" + new string(screen.CapturedOutput.TakeLast(3000).ToArray()));
        }
    }

    private static async Task<string> WaitForFrameAsync(PtyScreenCapture screen, Func<string, bool> predicate,
        CancellationToken token)
    {
        try
        {
            return await screen.WaitForFrameAsync(predicate).WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Timed out waiting for frame. Last frame:\n" + screen.LastFrame +
                "\nOutput tail:\n" + new string(screen.CapturedOutput.TakeLast(3000).ToArray()));
        }
    }

    private static async Task<string> WaitForRequestAsync(ConcurrentQueue<string> requests, int count,
        CancellationToken token)
    {
        while (requests.Count < count) await Task.Delay(10, token);
        return requests.ToArray()[count - 1];
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
