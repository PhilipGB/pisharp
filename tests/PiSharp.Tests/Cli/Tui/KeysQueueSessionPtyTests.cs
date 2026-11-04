using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class KeysQueueSessionPtyTests
{
    [Fact]
    public async Task PickerPathSortNamedScopeRenameAndDeleteWorkThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-picker-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        Process? process = null;
        try
        {
            var store = new ConversationStore(root, Path.Combine(root, "sessions"));
            var current = new ConversationSession(root, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            current.Rename("current session");
            current.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "current"));
            var currentPath = store.NewPath(current);
            await store.SaveAsync(current, currentPath);
            foreach (var name in new[] { "alpha session", "beta session" })
            {
                var session = new ConversationSession(root, ConnectionSettings.LocalModel,
                    new Uri(ConnectionSettings.LocalEndpoint).ToString());
                session.Rename(name);
                session.Append(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, name));
                await store.SaveAsync(session, store.NewPath(session));
                await Task.Delay(5);
            }
            var unnamed = new ConversationSession(root, ConnectionSettings.LocalModel,
                new Uri(ConnectionSettings.LocalEndpoint).ToString());
            await store.SaveAsync(unnamed, store.NewPath(unnamed));
            await Task.Delay(5);

            process = Start(root, agent,
                $"--local --offline --session {Quote(currentPath)} --session-dir {Quote(store.DirectoryPath)} --no-tools");
            var capture = new OutputCapture(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await capture.InputReady.Task.WaitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Picker PTY did not become ready. stdout:\n" + capture.Frame() +
                    "\nstderr:\n" + await capture.ErrorCompletion);
            }

            await Write(process, "/resume\r");
            await capture.WaitFor("Resume Session (Current Folder)", timeout.Token);
            await capture.WaitFor("Sort: Threaded", timeout.Token);

            await Write(process, "\u000e"); // Ctrl+N named-only
            await capture.WaitFor("Name: Named", timeout.Token);
            await Write(process, "\u0010"); // Ctrl+P path display
            await capture.WaitFor("path (on)", timeout.Token);
            Assert.Contains("pisharp-picker-pty-", capture.Frame());
            Assert.DoesNotContain("Switched to", capture.Frame());

            await Write(process, "\u0013"); // Ctrl+S -> Recent
            await capture.WaitFor("Sort: Recent", timeout.Token);
            await Write(process, "\u0013"); // Ctrl+S -> Fuzzy
            await capture.WaitFor("Sort: Fuzzy", timeout.Token);
            await Write(process, "\u0013"); // Ctrl+S -> Threaded
            await capture.WaitFor("Sort: Threaded", timeout.Token);
            Assert.DoesNotContain("Model selection saved", capture.Frame());
            Assert.DoesNotContain("Thinking level:", capture.Frame());

            await Write(process, "\t"); // Tab -> all-project scope
            await capture.WaitFor("Resume Session (All)", timeout.Token);
            await capture.WaitFor("Scope: All", timeout.Token);
            await Write(process, "\t"); // Tab -> current project
            await capture.WaitFor("Scope: Current Folder", timeout.Token);

            await Write(process, "alpha");
            await capture.WaitFor("Search: alpha", timeout.Token);
            var alphaPath = (await SessionCatalog.ListAsync(store)).Single(item => item.Name == "alpha session").Path;
            await Write(process, "\u001b[127;5u"); // Modified Backspace is ignored by Pi's search input.
            await Task.Delay(100, timeout.Token);
            Assert.Contains("Search: alpha", capture.Frame());
            Assert.True(File.Exists(alphaPath));

            await Write(process, "\u007f\u007f\u007f\u007f\u007f"); // clear search
            await Task.Delay(100, timeout.Token);
            await Write(process, "\u001b[B"); // select the next (non-current) session
            await Write(process, "\u001b[127;5u"); // Ctrl+Backspace on an empty query starts confirmation.
            await capture.WaitFor("Delete ", timeout.Token);
            Assert.True(File.Exists(alphaPath));
            await Write(process, "\u001b"); // Escape cancels confirmation.
            await Task.Delay(100, timeout.Token);
            Assert.True(File.Exists(alphaPath));

            // The active session cannot be deleted.
            await Write(process, "current session");
            await capture.WaitFor("Search: current session", timeout.Token);
            await Write(process, "\u0004"); // Ctrl+D
            await capture.WaitFor("Cannot delete the currently active session", timeout.Token);
            Assert.True(File.Exists(currentPath));

            // Rename a non-current session and verify persistence after reopening.
            await Write(process, "\u0015alpha");
            await capture.WaitFor("Search: alpha", timeout.Token);
            await Write(process, "\u0012"); // Ctrl+R
            await capture.WaitFor("Rename Session", timeout.Token);
            await Write(process, "\u0015renamed alpha\r"); // Clear the prefilled name, then save the new one.
            await capture.WaitFor("renamed alpha", timeout.Token);
            await capture.WaitFor("renamed alpha · ", timeout.Token);
            var afterRename = await SessionCatalog.ListAsync(store);
            Assert.True(afterRename.Any(item => item.Name == "renamed alpha"),
                "Rename did not persist. Output tail:\n" + capture.Frame()[^3000..]);

            // Delete a non-current session after explicit confirmation.
            await Write(process, "\u0015beta");
            await capture.WaitFor("Search: beta", timeout.Token);
            var betaPath = (await SessionCatalog.ListAsync(store)).Single(item => item.Name == "beta session").Path;
            await Write(process, "\u0004");
            await capture.WaitFor("Delete beta session?", timeout.Token);
            Assert.True(File.Exists(betaPath));
            await Write(process, "\r");
            await capture.WaitForAny(["Session deleted", "Session moved to trash"], timeout.Token);
            Assert.False(File.Exists(betaPath));
            await Write(process, "\u001b"); // close picker
            await Task.Delay(100, timeout.Token);
            await Write(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Unhandled exception", await capture.ErrorCompletion);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FollowUpDequeueRestoresSteeringFollowUpAndDraftWithoutPrematureExecutionThroughLinuxPty()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-queue-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        using var listener = StartListener(out var port);
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var requests = new ConcurrentQueue<string>();
        var firstRequestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(serverTimeout.Token);
                using var reader = new StreamReader(context.Request.InputStream);
                requests.Enqueue(await reader.ReadToEndAsync(serverTimeout.Token));
                if (index == 0)
                {
                    firstRequestSeen.TrySetResult();
                    await releaseFirst.Task.WaitAsync(serverTimeout.Token);
                }
                context.Response.ContentType = "text/event-stream";
                context.Response.KeepAlive = false;
                await using var writer = new StreamWriter(context.Response.OutputStream);
                await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_" + index +
                    "\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_" + index +
                    "\",\"output_index\":0,\"content_index\":0,\"delta\":\"" + (index == 0 ? "QUEUE_REPLY" : "RESTORED_REPLY") + "\"}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_" + index +
                    "\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                context.Response.Close();
            }
        });
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:PORT/v1","apiKeyEnv":"PISHARP_QUEUE_KEY","models":[{"id":"fixture-model","api":"openai-responses","reasoning":true}]}}}
                """.Replace("PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            process = Start(root, agent,
                "--provider fixture --model fixture-model --no-session --offline --no-tools",
                new Dictionary<string, string> { ["PISHARP_QUEUE_KEY"] = "fixture-key" });
            var capture = new OutputCapture(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await capture.InputReady.Task.WaitAsync(timeout.Token);

            await Write(process, "first prompt\r");
            await firstRequestSeen.Task.WaitAsync(timeout.Token);
            await Write(process, "steer one\r");
            await capture.WaitFor("Steering: steer one", timeout.Token);
            await Write(process, "\u001b[13;3u"); // Alt+Enter
            await Write(process, "follow one");
            await Task.Delay(100, timeout.Token);
            await Write(process, "\u001b[13;3u"); // Alt+Enter queues the follow-up.
            await capture.WaitFor("Follow-up: follow one", timeout.Token);
            await Write(process, "draft text");
            await capture.WaitFor("draft text", timeout.Token);
            await Write(process, "\u001b[1;3A"); // Alt+Up restores steering, follow-up, then the draft.
            await capture.WaitFor("Restored 2 queued messages to editor", timeout.Token);
            Assert.Contains("steer one", capture.Frame());
            Assert.Contains("follow one", capture.Frame());
            Assert.Contains("draft text", capture.Frame());

            releaseFirst.TrySetResult();
            await capture.WaitFor("QUEUE_REPLY", timeout.Token);
            Assert.Single(requests);

            await Write(process, "\r"); // submit the restored combined draft
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (requests.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(25, timeout.Token);
            Assert.Equal(2, requests.Count);
            var restoredRequest = requests.ToArray()[1];
            Assert.Contains("steer one\\n\\nfollow one\\n\\ndraft text", restoredRequest, StringComparison.Ordinal);
            await capture.WaitFor("RESTORED_REPLY", timeout.Token);
            await Write(process, "/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Unhandled exception", await capture.ErrorCompletion);
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

    private static Process Start(string cwd, string agent, string arguments,
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
        start.ArgumentList.Add($"stty rows 30 cols 100; exec dotnet {Quote(typeof(CliArguments).Assembly.Location)} {arguments}");
        start.ArgumentList.Add("/dev/null");
        start.Environment["PISHARP_AGENT_DIR"] = agent;
        start.Environment["TERM"] = "xterm-256color";
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        return Process.Start(start)!;
    }

    private static Task Write(Process process, string value) => process.StandardInput.WriteAsync(value);

    private static HttpListener StartListener(out int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return listener;
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private sealed class OutputCapture
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly object _gate = new();
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OutputCapture(Process process)
        {
            _process = process;
            _ = Task.Run(async () =>
            {
                var buffer = new char[4096];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (_gate)
                    {
                        _output.Append(buffer, 0, count);
                        var changed = _changed;
                        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        changed.TrySetResult();
                        if (_output.ToString().Contains("\u001b[?2004h", StringComparison.Ordinal))
                            InputReady.TrySetResult();
                    }
                }
                lock (_gate) _changed.TrySetResult();
            });
            ErrorCompletion = process.StandardError.ReadToEndAsync();
        }

        public TaskCompletionSource InputReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> ErrorCompletion { get; }

        public string Frame()
        {
            lock (_gate) return _output.ToString();
        }

        public async Task WaitFor(string value, CancellationToken cancellationToken) =>
            await WaitForAny([value], cancellationToken);

        public async Task WaitForAny(IReadOnlyList<string> values, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    var output = _output.ToString();
                    if (values.Any(value => output.Contains(value, StringComparison.Ordinal))) return;
                    changed = _changed.Task;
                }
                try { await changed.WaitAsync(cancellationToken); }
                catch (OperationCanceledException)
                {
                    var output = Frame();
                    throw new TimeoutException($"Timed out waiting for {string.Join(" or ", values)}. Output tail:\n" +
                        output[Math.Max(0, output.Length - 4000)..]);
                }
            }
        }
    }
}
