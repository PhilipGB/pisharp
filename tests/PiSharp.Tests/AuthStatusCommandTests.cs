using System.Diagnostics;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class AuthStatusCommandTests
{
    [Fact]
    public async Task OfflineStatusRequiresConfiguredProviderAndNeverPrintsOrBorrowsCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(agentDirectory);
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY",
                 "models":[{"id":"fixture-model"}]}}}
                """);
            async Task<(int ExitCode, string Output, string Error)> Run(params string[] args)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
                start.ArgumentList.Add("auth");
                foreach (var arg in args) start.ArgumentList.Add(arg);
                foreach (var name in new[] { "PISHARP_FIXTURE_KEY", "PISHARP_BASE_URL", "PISHARP_API_KEY", "PISHARP_MODELS_PATH", "PISHARP_AUTH_PATH" })
                    start.Environment.Remove(name);
                start.Environment["OPENAI_API_KEY"] = "unrelated-openai-key";
                start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                return (process.ExitCode, await output, await error);
            }
            var missing = await Run("check", "--provider", "fixture", "--model", "fixture-model");
            Assert.Equal(1, missing.ExitCode);
            Assert.Contains("fixture/fixture-model: not authenticated", missing.Output);
            Assert.DoesNotContain("unrelated-openai-key", missing.Output + missing.Error);
            var auth = new AuthStorage(Path.Combine(agentDirectory, "auth.json"));
            await auth.StoreApiKeyAsync("fixture", "fixture-auth-check-secret");
            var present = await Run("check", "--provider", "fixture", "--model", "fixture-model");
            Assert.Equal(0, present.ExitCode);
            Assert.Contains("credential available (stored API key)", present.Output);
            Assert.Contains("No provider connection was attempted", present.Output);
            Assert.DoesNotContain("fixture-auth-check-secret", present.Output + present.Error);
            Assert.DoesNotContain("unrelated-openai-key", present.Output + present.Error);
            Assert.Equal(2, (await Run("check", "--provider", "fixture", "--credentials")).ExitCode);
            Assert.Equal(2, (await Run("check", "--provider", "fixture", "--model", "other")).ExitCode);
            Assert.Empty(Directory.EnumerateFiles(root, "*.session.json", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }
}
