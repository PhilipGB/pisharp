using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Cli;

namespace PiSharp.Tests;

internal sealed class RadiusLoginTerminal : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly object _gate = new();
    private TaskCompletionSource _changed = NewSignal();
    private readonly Task _readOutput;
    private readonly Task<string> _readError;
    private bool _closed;

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-radius-login-" + Guid.NewGuid().ToString("N"));
    public string AgentDirectory => Path.Combine(Root, "agent");
    public string McpPath => Path.Combine(AgentDirectory, "mcp.json");
    public string AuthPath => Path.Combine(AgentDirectory, "auth.json");
    public int Mark { get { lock (_gate) return _output.Length; } }
    public string Output { get { lock (_gate) return _output.ToString(); } }

    public RadiusLoginTerminal(string gateway, string initialMcp, bool customInterrupt = false, bool trueColor = false,
        bool browserFixture = false)
    {
        Directory.CreateDirectory(AgentDirectory);
        File.WriteAllText(Path.Combine(AgentDirectory, "settings.json"), "{\"quietStartup\":true}");
        File.WriteAllText(McpPath, initialMcp);
        if (customInterrupt)
            File.WriteAllText(Path.Combine(AgentDirectory, "keybindings.json"), "{\"app.interrupt\":\"ctrl+q\"}");
        var start = new ProcessStartInfo("/usr/bin/script")
        {
            WorkingDirectory = Root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-q", "-e", "-c", "stty rows 40 cols 120; exec dotnet " +
                ShellQuote(typeof(CliArguments).Assembly.Location) + " --local --offline --no-session --no-tools --no-approve", "/dev/null" }
        };
        foreach (var name in new[] { "RADIUS_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_SETTINGS_PATH", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
            start.Environment.Remove(name);
        start.Environment["PISHARP_AGENT_DIR"] = AgentDirectory;
        start.Environment["PISHARP_RADIUS_GATEWAY"] = gateway;
        if (browserFixture)
        {
            foreach (var name in new[] { "DISPLAY", "WAYLAND_DISPLAY", "TERMUX_VERSION", "SSH_CONNECTION", "SSH_CLIENT", "MOSH_CONNECTION" })
                start.Environment.Remove(name);
            var bin = Path.Combine(Root, "bin");
            Directory.CreateDirectory(bin);
            var openBrowser = Path.Combine(bin, "xdg-open");
            File.WriteAllText(openBrowser, "#!/bin/sh\nexit 0\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(openBrowser, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
        if (trueColor)
        {
            start.Environment.Remove("NO_COLOR");
            start.Environment["TERM"] = "xterm-256color";
            start.Environment["COLORTERM"] = "truecolor";
            start.Environment["PI_TRUE_COLOR"] = "1";
        }
        _process = Process.Start(start)!;
        _readOutput = ReadOutputAsync();
        _readError = _process.StandardError.ReadToEndAsync();
    }

    public async Task SendAsync(string input)
    {
        await _process.StandardInput.WriteAsync(input);
        await _process.StandardInput.FlushAsync();
    }

    public Task<string> WaitTextAsync(string text, int after = 0) =>
        WaitAsync(output => StripAnsi(output[after..]).Contains(text, StringComparison.Ordinal)
            ? output[after..] : null);

    public string ReadAuthorizationUrl(int after = 0)
    {
        const string marker = "\u001b]8;;";
        var output = Output;
        for (var offset = after; (offset = output.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0;)
        {
            var start = offset + marker.Length;
            var bell = output.IndexOf('\u0007', start);
            var stringTerminator = output.IndexOf("\u001b\\", start, StringComparison.Ordinal);
            var end = bell < 0 ? stringTerminator : stringTerminator < 0 ? bell : Math.Min(bell, stringTerminator);
            if (end < 0) break;
            var candidate = output[start..end];
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                return candidate;
            offset = end + 1;
        }
        throw new InvalidOperationException("The Radius sign-in URL was not rendered as a terminal link.");
    }

    public Task<string> WaitFrameAsync(Func<string, bool> predicate, int after = 0, bool preserveAnsi = false) =>
        WaitAsync(output =>
        {
            foreach (var frame in TerminalOutputFrameReader.Read(output, rows: 40, columns: 120))
            {
                if (frame.Start < after) continue;
                var candidate = preserveAnsi ? frame.Output : frame.Screen;
                if (predicate(candidate)) return candidate;
            }
            return null;
        });

    public Task<string> WaitEditorAsync(int after = 0) => WaitFrameAsync(frame =>
        frame.Contains("Qwen3.8-27B-GGUF", StringComparison.Ordinal) &&
        !frame.Contains("Enter select · Esc close", StringComparison.Ordinal), after);

    public async Task OpenRadiusFromMenuAsync()
    {
        await WaitEditorAsync();
        var mark = Mark;
        await SendAsync("/login\n");
        await WaitTextAsync("Select authentication method:", mark);
        mark = Mark;
        await SendAsync("Radius\n");
        await WaitTextAsync("Select an OAuth login method:", mark);
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

    private async Task<string> WaitAsync(Func<string, string?> match)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (match(_output.ToString()) is { } result) return result;
                if (_closed) throw new InvalidOperationException("The Radius terminal closed before the expected scene.\n" + _output);
                changed = _changed.Task;
            }
            try { await changed.WaitAsync(deadline.Token); }
            catch (OperationCanceledException error)
            {
                var output = Output;
                throw new TimeoutException("The Radius terminal did not reach the expected scene.\n" +
                    output[^Math.Min(output.Length, 6000)..], error);
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
        Directory.Delete(Root, recursive: true);
    }
}
