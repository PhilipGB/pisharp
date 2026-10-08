using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class LlamaRouterLoginTuiTests
{
    [Fact]
    public async Task LoginPromptsForRouterUrlAndOptionalKeyAndCancellationDoesNotPersistCredentials()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-login-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer();
        await using var terminal = new LoginTerminal(root, agent, server.Origin);
        try
        {
            await terminal.WaitEditorAsync();
            var mark = terminal.Mark;
            await terminal.SendAsync("/login llama.cpp\n");
            await terminal.WaitTextAsync("llama.cpp server URL", mark);
            mark = terminal.Mark;
            await terminal.SendAsync(server.Origin + "/v1/\n");
            await terminal.WaitTextAsync("API key (optional)", mark);
            mark = terminal.Mark;
            await terminal.SendAsync("\u001b");
            Assert.False(File.Exists(Path.Combine(agent, "auth.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoginUsesEnvironmentUrlWhenPromptAndOptionalKeyAreBlank()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-llama-login-env-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), """
            {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKey":"fixture-key","models":[{"id":"fixture-model"}]}}}
            """);
        await using var server = new RouterServer();
        await using var terminal = new LoginTerminal(root, agent, server.Origin + "/v1/");
        try
        {
            await terminal.WaitEditorAsync();
            var mark = terminal.Mark;
            await terminal.SendAsync("/login llama.cpp\n");
            await terminal.WaitTextAsync("llama.cpp server URL", mark);
            mark = terminal.Mark;
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("API key (optional)", mark);
            mark = terminal.Mark;
            await terminal.SendAsync("\n");
            await terminal.WaitTextAsync("Saved API key for llama.cpp", mark);

            Assert.Equal("/models", Assert.Single(server.Requests).Path);
            Assert.Null(server.Requests.Single().Authorization);
            var credential = await new AuthStorage(Path.Combine(agent, "auth.json")).ReadAsync("llama.cpp");
            Assert.Null(credential?.Key);
            Assert.Equal(server.Origin, credential?.Env?["LLAMA_BASE_URL"]);
            Assert.DoesNotContain("Bearer", terminal.Output);
            await terminal.QuitAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class RouterServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serve;
        public ConcurrentQueue<(string Path, string? Authorization)> Requests { get; } = new();
        public string Origin { get; }

        public RouterServer()
        {
            var (listener, port) = TestLoopbackListener.Start();
            _listener = listener;
            Origin = "http://127.0.0.1:" + port;
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                    Requests.Enqueue((context.Request.Url!.PathAndQuery, context.Request.Headers["Authorization"]));
                    var bytes = Encoding.UTF8.GetBytes("{\"data\":[]}");
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token);
                    context.Response.Close();
                }
            }
            catch (Exception error) when (_shutdown.IsCancellationRequested &&
                error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException)
            { }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Close();
            await _serve;
            _shutdown.Dispose();
        }
    }

    private sealed class LoginTerminal : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _output = new();
        private readonly object _gate = new();
        private TaskCompletionSource _changed = NewSignal();
        private readonly Task _readOutput;
        private readonly Task<string> _readError;
        private bool _closed;

        public string Output { get { lock (_gate) return _output.ToString(); } }
        public int Mark { get { lock (_gate) return _output.Length; } }

        public LoginTerminal(string root, string agent, string llamaBaseUrl)
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
            start.ArgumentList.Add("stty rows 30 cols 100; exec dotnet " + ShellQuote(typeof(CliArguments).Assembly.Location) +
                " --provider fixture --model fixture-model --offline --no-session --no-tools");
            start.ArgumentList.Add("/dev/null");
            foreach (var name in new[] { "LLAMA_BASE_URL", "LLAMA_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL",
                         "PISHARP_MODEL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["LLAMA_BASE_URL"] = llamaBaseUrl;
            _process = Process.Start(start)!;
            _readOutput = ReadOutputAsync();
            _readError = _process.StandardError.ReadToEndAsync();
        }

        public async Task SendAsync(string input)
        {
            await _process.StandardInput.WriteAsync(input);
            await _process.StandardInput.FlushAsync();
        }

        public Task<string> ReadErrorAsync() => _readError;

        public Task<string> WaitTextAsync(string value, int after = 0) => WaitAsync(output =>
            StripAnsi(output[after..]).Contains(value, StringComparison.Ordinal) ? output[after..] : null);

        public Task<string> WaitEditorAsync(int after = 0) => WaitFrameAsync(frame =>
            frame.Contains("fixture-model", StringComparison.Ordinal), after);

        private Task<string> WaitFrameAsync(Func<string, bool> predicate, int after) => WaitAsync(output =>
        {
            const string start = "\u001b[?2026h";
            const string end = "\u001b[?2026l";
            var position = after;
            while ((position = output.IndexOf(start, position, StringComparison.Ordinal)) >= 0)
            {
                var close = output.IndexOf(end, position + start.Length, StringComparison.Ordinal);
                if (close < 0) return null;
                var frame = StripAnsi(output[(position + start.Length)..close]);
                if (predicate(frame)) return frame;
                position = close + end.Length;
            }
            return null;
        });

        private async Task<string> WaitAsync(Func<string, string?> match)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (match(_output.ToString()) is { } result) return result;
                    if (_closed) throw new InvalidOperationException("The llama.cpp login terminal closed early.\n" + _output);
                    changed = _changed.Task;
                }
                try { await changed.WaitAsync(deadline.Token); }
                catch (OperationCanceledException error)
                {
                    var output = Output;
                    throw new TimeoutException("The llama.cpp login terminal did not reach the expected prompt.\n" +
                        output[^Math.Min(output.Length, 5000)..], error);
                }
            }
        }

        private async Task ReadOutputAsync()
        {
            var buffer = new char[4096];
            while (await _process.StandardOutput.ReadAsync(buffer) is var count && count > 0)
            {
                lock (_gate)
                {
                    _output.Append(buffer, 0, count);
                    _changed.TrySetResult();
                    _changed = NewSignal();
                }
            }
            lock (_gate) { _closed = true; _changed.TrySetResult(); }
        }

        public async Task QuitAsync()
        {
            await SendAsync("/quit\n");
            _process.StandardInput.Close();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await _readOutput;
            Assert.Equal(0, _process.ExitCode);
            Assert.Equal("", await _readError);
        }

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        private static string StripAnsi(string text) => Regex.Replace(text, "\u001b\\[[0-9;?]*[A-Za-z]", "");

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            await _readOutput;
            await _readError;
            _process.Dispose();
        }
    }
}
