using System.Diagnostics;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class CliInformationalProcessTests
{
    [Fact]
    public async Task ProviderWithoutModelFailsBeforeSettingsCanSelectADefaultModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-model-required-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
                "{\"defaultProvider\":\"mistral\",\"defaultModel\":\"mistral-large-latest\"}");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "openai",
                         "--offline", "--no-session", "--print", "hello" })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                         "PISHARP_SETTINGS_PATH", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH" })
                start.Environment.Remove(name);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(2, process.ExitCode);
            Assert.Equal("", await output);
            Assert.Contains("--provider requires --model", await errors, StringComparison.Ordinal);
            Assert.Contains("--provider openai --model", await errors, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProviderWithoutModelErrorPrecedesProjectAndUserConfigurationLoading()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-model-early-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{invalid");
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "openai",
                         "--print", "hello" })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(2, process.ExitCode);
            Assert.Equal("", await output);
            var message = await errors;
            Assert.Contains("--provider requires --model", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Invalid settings", message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
                start.ArgumentList.Add("--provider");
                start.ArgumentList.Add("openai");
                if (flag == "--help")
                    foreach (var argument in new[] { "--theme", "themes/custom.json", "--use-theme", "custom", "--no-themes" })
                        start.ArgumentList.Add(argument);
                start.Environment["PISHARP_AGENT_DIR"] = agent;
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("", await error);
                if (flag.EndsWith('h') || flag == "--help")
                {
                    var help = await output;
                    Assert.Contains("Usage: pisharp", help);
                    Assert.Contains("--theme <path>", help);
                    Assert.Contains("--use-theme <name[/name]>", help);
                    Assert.Contains("--no-themes", help);
                    Assert.Contains("--provider <id> filters explicit model selection and requires --model <id>", help);
                }
                else Assert.Equal(typeof(CliArguments).Assembly.GetName().Version?.ToString(3) + "\n", await output);
            }
            Assert.True(CliArguments.Parse(["-p"]).Print);
            Assert.True(CliArguments.Parse(["-c"]).Continue);
            Assert.Equal("-v", CliArguments.Parse(["--", "-v"]).Prompt);
        }
        finally { Directory.Delete(root, true); }
    }
}
