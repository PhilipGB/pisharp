using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProviderLoginTuiTests
{
    [Fact]
    public async Task InteractiveLoginAndLogoutNeverSendUnauthenticatedPromptsOrEchoSecret()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-pty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var agentDirectory = Path.Combine(root, "agent");
            Directory.CreateDirectory(agentDirectory);
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://127.0.0.1:1/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY",
                 "models":[{"id":"fixture-model","reasoning":false},{"id":"fixture-alt","reasoning":false}]}}}
                """);
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"stty rows 24 cols 80; exec dotnet '{typeof(CliArguments).Assembly.Location}' --provider fixture --model fixture-model --no-tools --offline --session-dir '{root}/sessions'");
            start.ArgumentList.Add("/dev/null");
            foreach (var name in new[] { "PISHARP_FIXTURE_KEY", "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = new StringBuilder();
            var draining = Task.Run(async () =>
            {
                var buffer = new char[1024];
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer)) > 0)
                    lock (output) output.Append(buffer, 0, count);
            });
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            async Task WaitFor(string text)
            {
                while (true)
                {
                    lock (output) if (output.ToString().Contains(text, StringComparison.Ordinal)) return;
                    await Task.Delay(20, timeout.Token);
                }
            }
            try
            {
                await process.StandardInput.WriteAsync("before-login\n/login fixture oauth\n/login fixture api-key\n");
                await process.StandardInput.FlushAsync();
                await WaitFor("API key for fixture:");
                await process.StandardInput.WriteAsync("fixture-secret-not-for-transcript\n");
                await process.StandardInput.FlushAsync();
                await WaitFor("Authenticated fixture with api-key");
                await process.StandardInput.WriteAsync("/model fixture/fixture-alt\n/thinking high\n/thinking\n/model\n\u001b[B\u001b[A\r/logout\nafter-logout\n/quit\n");
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                await draining;
                var transcript = output.ToString();
                Assert.Equal(1, process.ExitCode); // Rejected prompts are failures, not successful inference.
                Assert.Contains("has no configured OAuth adapter", transcript);
                Assert.Contains("Authenticated fixture with api-key", transcript);
                Assert.Contains("Model: fixture-alt", transcript);
                Assert.Contains("Thinking: off", transcript);
                Assert.Contains("Thinking: off; available: off", transcript);
                Assert.Contains("Logged out fixture", transcript);
                var restoreBoundary = transcript.LastIndexOf("\u001b[?1049l", StringComparison.Ordinal);
                Assert.True(restoreBoundary >= 0, "The interactive screen was not restored.");
                var restoredScrollback = transcript[(restoreBoundary + "\u001b[?1049l".Length)..];
                Assert.Equal(2, restoredScrollback.Split("is not authenticated. Use /login", StringSplitOptions.None).Length - 1);
                Assert.DoesNotContain("fixture-secret-not-for-transcript", transcript);
                Assert.DoesNotContain("Connection refused", transcript);
                Assert.DoesNotContain("fixture-secret-not-for-transcript", await stderr);
                var store = new PiSharp.Runtime.Sessions.ConversationStore(root, Path.Combine(root, "sessions"));
                var session = await store.LoadAsync(Assert.Single(Directory.GetFiles(store.DirectoryPath, "*.session.json")));
                Assert.Empty(session.ActiveMessages());
                Assert.Equal("fixture-alt", session.Model);
                Assert.Equal("fixture", session.Provider);
                Assert.Null(await new AuthStorage(Path.Combine(agentDirectory, "auth.json")).ReadAsync("fixture"));
            }
            catch
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
        }
        finally { Directory.Delete(root, true); }
    }

}
