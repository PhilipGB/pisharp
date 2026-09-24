using System.Diagnostics;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class CliInformationalProcessTests
{
    [Fact]
    public async Task HelpAndVersionDoNotReadInvalidProjectOrUserConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-help-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{invalid");
            foreach (var flag in new[] { "-h", "--help", "-v", "--version" })
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
                start.ArgumentList.Add(flag);
                start.Environment["PISHARP_AGENT_DIR"] = agent;
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("", await error);
                if (flag.EndsWith('h') || flag == "--help") Assert.Contains("Usage: pisharp", await output);
                else Assert.Equal(typeof(CliArguments).Assembly.GetName().Version?.ToString(3) + "\n", await output);
            }
            Assert.True(CliArguments.Parse(["-p"]).Print);
            Assert.True(CliArguments.Parse(["-c"]).Continue);
            Assert.Equal("-v", CliArguments.Parse(["--", "-v"]).Prompt);
        }
        finally { Directory.Delete(root, true); }
    }
}
