using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class QuietStartupTests
{
    [Theory]
    [InlineData("\"header\"", "Header")]
    [InlineData("1", "Full")]
    [InlineData("\"true\"", "Full")]
    [InlineData("\"HEADER\"", "Full")]
    [InlineData("null", "Full")]
    [InlineData("{}", "Full")]
    public async Task HeaderModeAndInvalidValuesFollowCurrentPi(string json, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-value-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), $"{{\"quietStartup\":{json}}}");
            Assert.Equal(expected, (await UserSettings.LoadAsync(root, _ => null)).QuietStartup.ToString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task HeaderModeCanBeSavedAndRemovedWithoutChangingOtherSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"hideThinkingBlock\":true}");
            await UserSettingsWriter.SetAsync(path, "quietStartup", "header", userScope: true);
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.Equal("header", json.RootElement.GetProperty("quietStartup").GetString());
                Assert.True(json.RootElement.GetProperty("hideThinkingBlock").GetBoolean());
            }
            Assert.Equal("Header", (await UserSettings.LoadAsync(root, _ => null)).QuietStartup.ToString());
            await UserSettingsWriter.SetAsync(path, "quietStartup", null, userScope: true);
            Assert.Null((await UserSettings.LoadAsync(root, _ => null)).QuietStartup);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrustedProjectCanOverrideQuietStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"quietStartup\":true}");
            var user = await UserSettings.LoadAsync(root, _ => null);
            Assert.Equal(QuietStartupMode.Silent, user.QuietStartup);
            Assert.True(CliArguments.Parse(["--verbose"]).Verbose);
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), "{\"quietStartup\":false}");
            Assert.Equal(QuietStartupMode.Full, user.Overlay(await UserSettings.LoadProjectAsync(root)).QuietStartup);
            await File.WriteAllTextAsync(path, "{\"quietStartup\":1}");
            Assert.Equal(QuietStartupMode.Full, (await UserSettings.LoadAsync(root, _ => null)).QuietStartup);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("true", "\"header\"", QuietStartupMode.Header)]
    [InlineData("\"header\"", "false", QuietStartupMode.Full)]
    [InlineData("\"header\"", "true", QuietStartupMode.Silent)]
    [InlineData("\"header\"", "null", QuietStartupMode.Full)]
    public async Task TrustedProjectOverridesEachStartupMode(string user, string project, QuietStartupMode expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), $"{{\"quietStartup\":{user}}}");
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), $"{{\"quietStartup\":{project}}}");
            var settings = await UserSettings.LoadAsync(root, _ => null);
            Assert.Equal(expected, settings.Overlay(await UserSettings.LoadProjectAsync(root)).QuietStartup);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("true", false, false, false)]
    [InlineData("true", true, true, true)]
    [InlineData("\"header\"", false, true, false)]
    [InlineData("\"header\"", true, true, true)]
    [InlineData("false", false, true, true)]
    [InlineData("false", true, true, true)]
    public async Task TerminalStartupSeparatesHeaderAndDetails(string value, bool verbose, bool header, bool details)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), $"{{\"quietStartup\":{value}}}");
            var cli = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 40 cols 120; dotnet '{cli}' --local --no-session --no-tools --no-approve {(verbose ? "--verbose" : "")}", "/dev/null" }
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
            var text = await output;
            Assert.Equal(header, text.Contains("/model · /settings", StringComparison.Ordinal));
            Assert.Equal(header, text.Contains("PiSharp v", StringComparison.Ordinal));
            Assert.Equal(details, text.Contains("PiSharp · local/", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
