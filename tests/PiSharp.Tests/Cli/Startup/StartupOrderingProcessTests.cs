using System.Diagnostics;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class StartupOrderingProcessTests
{
    [Fact]
    public async Task InvalidConfigurationPrecedesProviderCatalogFailure()
    {
        await WithProcessAsync(async (root, agent, run) =>
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{invalid");
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), "{providerbroken");
            var result = await run(["--offline", "--no-session", "--print"], "");
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("'i' is an invalid start", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("'p' is an invalid start", result.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SessionNameIsSavedBeforeMissingPrintPromptIsRejected()
    {
        await WithProcessAsync(async (root, agent, run) =>
        {
            var sessions = Path.Combine(root, "sessions");
            var result = await run(["--offline", "--local", "--name", "prepared", "--session-dir", sessions, "--print"], "");
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("A prompt is required in print mode.\n", result.Error.ReplaceLineEndings("\n"));
            var store = new ConversationStore(root, sessions);
            var saved = await store.LoadAsync(Assert.Single(Directory.GetFiles(sessions, "*.session.json")));
            Assert.Equal("prepared", saved.Name);
        });
    }

    [Theory]
    [InlineData("print")]
    [InlineData("json")]
    public async Task RedirectedPromptIsAuthenticationGatedBeforeExecution(string mode)
    {
        await WithProcessAsync(async (root, agent, run) =>
        {
            var result = await run(["--offline", "--provider", "openai", "--model", "gpt-4o", "--no-session", "--mode", mode], "piped prompt");
            Assert.Equal(2, result.ExitCode);
            Assert.Equal("", result.Output);
            Assert.Contains("Provider 'openai' is not authenticated.", result.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RpcOwnsRedirectedInputAndAllowsUnauthenticatedStartup()
    {
        await WithProcessAsync(async (root, agent, run) =>
        {
            var result = await run(["--offline", "--provider", "openai", "--model", "gpt-4o", "--no-session", "--mode", "rpc"],
                "{\"id\":\"state\",\"type\":\"get_state\"}\n");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("", result.Error);
            Assert.Contains("\"id\":\"state\"", result.Output, StringComparison.Ordinal);
            Assert.Contains("\"success\":true", result.Output, StringComparison.Ordinal);
        });
    }

    private static async Task WithProcessAsync(Func<string, string,
        Func<string[], string, Task<(int ExitCode, string Output, string Error)>>, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-startup-order-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            async Task<(int, string, string)> RunAsync(string[] arguments, string input)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("PISHARP_", StringComparison.Ordinal) ||
                             name.EndsWith("_API_KEY", StringComparison.Ordinal) || name == "PI_OFFLINE").ToArray())
                    start.Environment.Remove(name);
                start.Environment["PISHARP_AGENT_DIR"] = agent;
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(timeout.Token); }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                return (process.ExitCode, await output, await error);
            }
            await body(root, agent, RunAsync);
        }
        finally { Directory.Delete(root, true); }
    }
}
