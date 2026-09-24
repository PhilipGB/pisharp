using System.Diagnostics;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProjectSettingsProcessTests
{
    [Fact]
    public async Task UntrustedProjectSettingsAreNeverParsedAndTrustedInvalidSettingsFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-project-settings-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), "{\"compaction\":{\"reserveTokens\":-1}}");
            async Task<(int Code, string Error)> Run(string trust)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var arg in new[] { typeof(CliArguments).Assembly.Location, trust, "--provider", "openai", "--model", "gpt-4o-mini", "--no-session", "--no-tools", "--print", "hello" })
                    start.ArgumentList.Add(arg);
                start.Environment["PISHARP_AGENT_DIR"] = agent;
                foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_MODEL", "PISHARP_BASE_URL", "PISHARP_SETTINGS_PATH" })
                    start.Environment.Remove(name);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                _ = await output;
                return (process.ExitCode, await error);
            }
            var denied = await Run("--no-approve");
            Assert.Equal(2, denied.Code);
            Assert.Contains("not authenticated", denied.Error);
            Assert.DoesNotContain("compaction", denied.Error);
            var allowed = await Run("--approve");
            Assert.True(allowed.Code == 2, $"Exit: {allowed.Code}; stderr: {allowed.Error}");
            Assert.Contains("compaction.reserveTokens", allowed.Error);
            Assert.DoesNotContain("not authenticated", allowed.Error);
        }
        finally { Directory.Delete(root, true); }
    }
}
