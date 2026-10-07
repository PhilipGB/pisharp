using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class CliThemeProcessTests
{
    [Fact]
    public async Task ExplicitThemePathAndUseThemeApplyThroughLinuxPtyWithNoThemes()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-theme-cli-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(cwd, "agent");
        var themeDirectory = Path.Combine(cwd, "themes");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(themeDirectory);
        Process? process = null;
        try
        {
            using var themeStream = typeof(TerminalTheme).Assembly
                .GetManifestResourceStream("PiSharp.Cli.Tui.Themes.dark.json");
            Assert.NotNull(themeStream);
            var theme = JsonNode.Parse(themeStream!)!.AsObject();
            theme["name"] = "cli-custom";
            theme["colors"]!["dim"] = "#010203";
            await File.WriteAllTextAsync(Path.Combine(themeDirectory, "custom.json"), theme.ToJsonString());

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
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --no-session --no-tools --theme themes/custom.json --use-theme cli-custom --no-themes");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            start.Environment["COLORTERM"] = "truecolor";
            start.Environment.Remove("NO_COLOR");
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var customThemeRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var drain = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
                {
                    output.Append(buffer, 0, count);
                    if (output.ToString().Contains("\u001b[38;2;1;2;3m", StringComparison.Ordinal))
                        customThemeRendered.TrySetResult();
                }
                return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await customThemeRendered.Task.WaitAsync(timeout.Token);
            await process.StandardInput.WriteAsync("/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            var captured = await drain;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("\u001b[38;2;1;2;3m", captured);
            Assert.DoesNotContain("Could not load theme", await stderr);
            Assert.DoesNotContain("Theme path does not exist", await stderr);
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(cwd, recursive: true);
        }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
