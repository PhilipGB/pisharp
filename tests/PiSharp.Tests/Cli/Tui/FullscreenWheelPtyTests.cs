using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

[Collection("Editor mutation PTY")]
public sealed class FullscreenWheelPtyTests
{
    [Fact]
    public async Task SettingsChangesApplyImmediatelyAndSurviveRestart()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var fixture = await Fixture.CreateAsync("1");
        await fixture.LaunchAsync();
        await fixture.WheelAsync(1, "startup-fixed-1");
        await fixture.SelectAsync("5");
        await fixture.WheelAsync(5, "live-fixed-5");
        await fixture.StopAsync();
        await fixture.LaunchAsync();
        await fixture.WheelAsync(5, "restart-fixed-5");
        await fixture.SelectAsync("auto");
        await fixture.WheelAsync(1, "live-auto-reset");
        await fixture.StopAsync();
        await fixture.LaunchAsync();
        await fixture.WheelAsync(1, "restart-auto-first");
        await fixture.StopAsync();
        await fixture.ExportAsync("live-persistence");
    }

    [Fact]
    public async Task InheritedCustomSevenCanBeSelectedAndPersistedInUserScope()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var fixture = await Fixture.CreateAsync(null, project: "7");
        await fixture.LaunchAsync();
        await fixture.SelectAsync("7");
        await fixture.WheelAsync(7, "selected-custom-7");
        await fixture.StopAsync();
        await fixture.LaunchAsync();
        await fixture.WheelAsync(7, "restart-fixed-7");
        await fixture.StopAsync();
        await fixture.ExportAsync("custom-seven");
    }

    [Theory]
    [InlineData(null, "fullscreen", 1)]
    [InlineData("\"auto\"", "fullscreen", 1)]
    [InlineData("5", "fullscreen", 5)]
    [InlineData("100", "regular", 0)]
    public async Task FirstWheelUsesStartupSettingsOnlyInFullscreen(string? raw, string mode, int distance)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var fixture = await Fixture.CreateAsync(raw, mode);
        await fixture.LaunchAsync();
        await fixture.WheelAsync(distance, "first-wheel");
        await fixture.StopAsync();
        await fixture.ExportAsync($"startup-{mode}-{raw ?? "unset"}");
    }

    [Fact]
    public async Task ProjectResetImmediatelyRestoresUserDistance()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var fixture = await Fixture.CreateAsync("7", project: "3");
        await fixture.LaunchAsync();
        await fixture.WheelAsync(3, "trusted-project-3");
        await fixture.SendAsync("/settings\r");
        await fixture.WaitAsync(s => s.Contains("User settings", StringComparison.Ordinal));
        await fixture.SendAsync("\t");
        await fixture.WaitAsync(s => s.Contains("Project settings", StringComparison.Ordinal));
        await fixture.SendAsync("Fullscreen wheel scrolling");
        await fixture.WaitAsync(s => s.Contains("Fullscreen wheel scrolling", StringComparison.Ordinal) &&
            s.Contains("Filter: Fullscreen wheel scrolling", StringComparison.Ordinal));
        await fixture.SendAsync("\u001b[3~");
        await fixture.WaitAsync(s => s.Contains("7 (inherited)", StringComparison.Ordinal));
        using (var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, ".pi/settings.json"))))
            Assert.False(saved.RootElement.TryGetProperty("fullscreenWheelScrollLines", out _));
        await fixture.SendAsync("\u001b");
        await fixture.WaitAsync(s => !s.Contains("Settings ·", StringComparison.Ordinal) && Fixture.FirstRow(s) > 0);
        await fixture.WheelAsync(7, "reset-inherited-7");
        await fixture.StopAsync();
        await fixture.ExportAsync("project-reset");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-wheel-pty-" + Guid.NewGuid().ToString("N"));
        private string Agent => Path.Combine(Root, "agent");
        private readonly List<object> _observations = [];
        private readonly List<string> _streams = [];
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(90));
        private Process? _process;
        private PtyScreenCapture? _screen;
        private Task<string>? _stdout;
        private Task<string>? _stderr;
        private string _path = "";

        internal static async Task<Fixture> CreateAsync(string? raw, string mode = "fullscreen", string? project = null)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Agent);
            var settings = "{\"quietStartup\":true,\"tuiMode\":\"" + mode + "\"" +
                (raw is null ? "" : ",\"fullscreenWheelScrollLines\":" + raw) + "}";
            await File.WriteAllTextAsync(Path.Combine(fixture.Agent, "settings.json"), settings);
            if (project is not null)
            {
                Directory.CreateDirectory(Path.Combine(fixture.Root, ".pi"));
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".pi/settings.json"),
                    "{\"fullscreenWheelScrollLines\":" + project + "}");
            }
            await File.WriteAllTextAsync(Path.Combine(fixture.Agent, "models.json"),
                "{\"providers\":{\"fixture\":{\"baseUrl\":\"http://127.0.0.1:1/v1\",\"apiKey\":\"fixture\",\"models\":[{\"id\":\"wheel-fixture\",\"api\":\"openai-completions\"}]}}}");
            var store = new ConversationStore(fixture.Root, Path.Combine(fixture.Root, "sessions"));
            var session = new ConversationSession(fixture.Root, "wheel-fixture", "http://127.0.0.1:1/v1", provider: "fixture");
            session.Append(new ChatMessage(ChatRole.User, "Seed transcript"));
            session.Append(new ChatMessage(ChatRole.Assistant, "```text\n" + string.Join('\n',
                Enumerable.Range(1, 300).Select(i => $"ROW-{i:000}")) + "\n```"));
            fixture._path = store.NewPath(session);
            await store.SaveAsync(session, fixture._path);
            return fixture;
        }

        internal async Task LaunchAsync()
        {
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = Root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 30 cols 100; exec dotnet '{typeof(CliArguments).Assembly.Location}' --offline --approve --session '{_path}' --no-tools", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = Agent;
            _process = Process.Start(start)!;
            _screen = new(_process, rows: 30, columns: 100);
            _stdout = _screen.ReadCompletion;
            _stderr = _process.StandardError.ReadToEndAsync();
            await _screen.InputReady.WaitAsync(_timeout.Token);
            await WaitAsync(s => FirstRow(s) > 0);
        }

        internal async Task SendAsync(string text)
        {
            await _process!.StandardInput.WriteAsync(text);
            await _process.StandardInput.FlushAsync();
        }

        internal async Task<string> WaitAsync(Func<string, bool> predicate)
        {
            try { return await _screen!.WaitForFrameAsync(predicate).WaitAsync(_timeout.Token); }
            catch (OperationCanceledException) { throw new InvalidOperationException("Wheel PTY timeout:\n" + _screen!.LastFrame); }
        }

        internal static int FirstRow(string frame)
        {
            var match = Regex.Match(frame, @"ROW-(\d{3})");
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }

        internal async Task WheelAsync(int expected, string checkpoint)
        {
            var barrier = "ready-" + checkpoint;
            await SendAsync(barrier);
            await WaitAsync(s => s.Contains(barrier, StringComparison.Ordinal));
            await SendAsync("\u0015");
            await WaitAsync(s => !s.Contains(barrier, StringComparison.Ordinal));
            var before = FirstRow(_screen!.LastFrame);
            Assert.True(before > 0, _screen.LastFrame);
            await SendAsync("\u001b[<64;3;2M");
            if (expected == 0)
            {
                // A keyboard echo is a process-owned barrier after the ignored regular-mode event.
                await SendAsync("wheel-barrier");
                await WaitAsync(s => s.Contains("wheel-barrier", StringComparison.Ordinal));
                await SendAsync("\u0015");
            }
            else
            {
                try { await WaitAsync(s => FirstRow(s) == before - expected); }
                catch (InvalidOperationException error)
                {
                    throw new InvalidOperationException($"{checkpoint}: before={before}, expected={expected}\n{error.Message}");
                }
            }
            var after = FirstRow(_screen.LastFrame);
            Assert.Equal(expected, before - after);
            _observations.Add(new { checkpoint, before, after, movement = before - after, frame = _screen.LastFrame });
        }

        internal async Task SelectAsync(string value)
        {
            await SendAsync("/settings\r");
            await WaitAsync(s => s.Contains("User settings", StringComparison.Ordinal));
            await SendAsync("Fullscreen wheel scrolling");
            await WaitAsync(s => s.Contains("Filter: Fullscreen wheel scrolling", StringComparison.Ordinal));
            await SendAsync("\r");
            await WaitAsync(s => s.Contains("Inherit / default", StringComparison.Ordinal));
            var index = value switch { "auto" => 1, "5" => 5, "7" => 6, _ => throw new ArgumentOutOfRangeException(nameof(value)) };
            await SendAsync("\u001b[H" + string.Concat(Enumerable.Repeat("\u001b[B", index)) + "\r");
            await WaitAsync(s => s.Contains("User settings", StringComparison.Ordinal) && !s.Contains("Inherit / default", StringComparison.Ordinal));
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Agent, "settings.json")));
            Assert.Equal(value, saved.RootElement.GetProperty("fullscreenWheelScrollLines").ToString());
            _observations.Add(new { checkpoint = "persist-" + value, stored = saved.RootElement.GetProperty("fullscreenWheelScrollLines").Clone() });
            await SendAsync("\u001b");
            await WaitAsync(s => !s.Contains("Settings ·", StringComparison.Ordinal) && FirstRow(s) > 0);
        }

        internal async Task StopAsync()
        {
            if (_process is null) return;
            await SendAsync("\u0015/quit\r");
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(_timeout.Token);
            _streams.Add(await _stdout!);
            Assert.Equal(0, _process.ExitCode);
            Assert.DoesNotContain("Agent error", await _stderr!);
            _process.Dispose();
            _process = null;
        }

        internal async Task ExportAsync(string name)
        {
            if (Environment.GetEnvironmentVariable("PISHARP_WHEEL_PTY_OUTPUT_DIR") is not { Length: > 0 } output) return;
            Directory.CreateDirectory(output);
            name = name.Replace('"', '_');
            await File.WriteAllTextAsync(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(new
            {
                observations = _observations,
                settings = await File.ReadAllTextAsync(Path.Combine(Agent, "settings.json"))
            }));
            for (var i = 0; i < _streams.Count; i++)
                await File.WriteAllTextAsync(Path.Combine(output, $"{name}-{i}.ansi"), _streams[i]);
        }

        public ValueTask DisposeAsync()
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.Dispose();
            _timeout.Dispose();
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
