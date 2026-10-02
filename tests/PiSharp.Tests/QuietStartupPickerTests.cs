using System.Diagnostics;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class QuietStartupPickerTests
{
    [Fact]
    public async Task SettingsPickerSavesHeaderModeThroughTerminal()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-picker-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"quietStartup\":true}");
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 40 cols 120; dotnet '{typeof(CliArguments).Assembly.Location}' --local --no-session --no-tools --no-approve", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            using var process = Process.Start(start)!;
            var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var output = Task.Run(async () =>
            {
                var text = new StringBuilder();
                var buffer = new char[4096];
                while (await process.StandardOutput.ReadAsync(buffer) is var count && count > 0)
                {
                    text.Append(buffer, 0, count);
                    var current = text.ToString();
                    if (current.Contains("Saved user setting quietStartup = header.", StringComparison.Ordinal)) saved.TrySetResult();
                    if (current.Contains("Settings closed.", StringComparison.Ordinal)) closed.TrySetResult();
                }
                return text.ToString();
            });
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteAsync("/settings\nQuiet startup\n\u001b[B\n");
                await process.StandardInput.FlushAsync();
                await saved.Task.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.Equal(QuietStartupMode.Header, (await UserSettings.LoadAsync(agent, _ => null)).QuietStartup);
                await process.StandardInput.WriteAsync("\u001b");
                await process.StandardInput.FlushAsync();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(12));
                await process.StandardInput.WriteAsync("/quit\n");
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("", await error);
                Assert.Contains("Disable verbose printing at startup (header: keep only the startup header)", await output);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail(await output);
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        finally { Directory.Delete(root, true); }
    }
}
