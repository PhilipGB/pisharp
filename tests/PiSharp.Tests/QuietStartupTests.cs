using System.Diagnostics;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class QuietStartupTests
{
    [Fact]
    public async Task QuietStartupValidatesAndTrustedProjectCanOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"quietStartup\":true}");
            var user = await UserSettings.LoadAsync(root, _ => null);
            Assert.True(user.QuietStartup);
            Assert.True(CliArguments.Parse(["--verbose"]).Verbose);
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), "{\"quietStartup\":false}");
            Assert.False(user.Overlay(await UserSettings.LoadProjectAsync(root)).QuietStartup);
            await File.WriteAllTextAsync(path, "{\"quietStartup\":1}");
            await Assert.ThrowsAsync<InvalidDataException>(() => UserSettings.LoadAsync(root, _ => null));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietStartupHidesTerminalBannerUnlessVerbose(bool verbose)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"quietStartup\":true}");
            var cli = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"dotnet '{cli}' --local --no-session --no-tools --no-approve {(verbose ? "--verbose" : "")}", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync("/quit\n");
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error);
            Assert.Equal(verbose, (await output).Contains("PiSharp · local/", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
