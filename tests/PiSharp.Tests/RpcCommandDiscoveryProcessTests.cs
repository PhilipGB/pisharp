using System.Diagnostics;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class RpcCommandDiscoveryProcessTests
{
    [Fact]
    public async Task GetCommandsProjectsPiSourceMetadataForExplicitResourcesAndExtensionCommands()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-commands-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var promptPath = Path.Combine(root, "prompts", "review.md");
        var longPromptPath = Path.Combine(root, "prompts", "long-description.md");
        var skillDirectory = Path.Combine(root, "skills", "quality-check");
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(promptPath)!);
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(promptPath,
            "---\ndescription: Review a change\n---\nReview this change carefully.\n");
        await File.WriteAllTextAsync(longPromptPath, new string('x', 70) + "\nAdditional instructions.\n");
        await File.WriteAllTextAsync(skillPath,
            "---\nname: quality-check\ndescription: Check quality\n---\nRun the checks.\n");

        try
        {
            using var response = await RunGetCommandsAsync(root, agentDirectory,
            [
                "--local", "--offline", "--no-session", "--no-skills", "--no-prompt-templates", "--no-extensions",
                "--skill", skillPath, "--prompt-template", promptPath, "--prompt-template", longPromptPath,
                "--extension", typeof(FixtureExtension).Assembly.Location
            ]);
            var rootElement = response.RootElement;
            Assert.Equal("commands", rootElement.GetProperty("id").GetString());
            Assert.True(rootElement.GetProperty("success").GetBoolean());
            var commands = rootElement.GetProperty("data").GetProperty("commands").EnumerateArray().ToArray();
            Assert.Equal(new[] { "fixture", "plain", "review", "long-description", "skill:quality-check" }, commands.Select(command =>
                command.GetProperty("name").GetString()));

            var extension = commands[0];
            Assert.Equal("Fixture extension command.", extension.GetProperty("description").GetString());
            Assert.Equal("extension", extension.GetProperty("source").GetString());
            var extensionSource = extension.GetProperty("sourceInfo");
            Assert.Equal(Path.GetFullPath(typeof(FixtureExtension).Assembly.Location), extensionSource.GetProperty("path").GetString());
            Assert.Equal("cli", extensionSource.GetProperty("source").GetString());
            Assert.Equal("temporary", extensionSource.GetProperty("scope").GetString());
            Assert.Equal("top-level", extensionSource.GetProperty("origin").GetString());
            Assert.False(extensionSource.TryGetProperty("baseDir", out _));

            Assert.False(commands[1].TryGetProperty("description", out _));
            Assert.Equal("extension", commands[1].GetProperty("source").GetString());

            var prompt = commands[2];
            Assert.Equal("Review a change", prompt.GetProperty("description").GetString());
            Assert.Equal("prompt", prompt.GetProperty("source").GetString());
            AssertSourceInfo(prompt.GetProperty("sourceInfo"), promptPath, Path.GetDirectoryName(promptPath)!);

            Assert.Equal(new string('x', 60) + "...", commands[3].GetProperty("description").GetString());
            AssertSourceInfo(commands[3].GetProperty("sourceInfo"), longPromptPath, Path.GetDirectoryName(longPromptPath)!);

            var skill = commands[4];
            Assert.Equal("Check quality", skill.GetProperty("description").GetString());
            Assert.Equal("skill", skill.GetProperty("source").GetString());
            AssertSourceInfo(skill.GetProperty("sourceInfo"), skillPath, skillDirectory);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task GetCommandsPreservesPiAutoResourceOrderAndScopeMetadata()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-auto-commands-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var userSkillDirectory = Path.Combine(agentDirectory, "skills", "user-check");
        var projectSkillDirectory = Path.Combine(root, ".pi", "skills", "project-check");
        Directory.CreateDirectory(Path.Combine(agentDirectory, "prompts"));
        Directory.CreateDirectory(userSkillDirectory);
        Directory.CreateDirectory(Path.Combine(root, ".pi", "prompts"));
        Directory.CreateDirectory(projectSkillDirectory);
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "prompts", "user-review.md"),
            "---\ndescription: User review\n---\nReview.\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".pi", "prompts", "project-review.md"),
            "---\ndescription: Project review\n---\nReview.\n");
        await File.WriteAllTextAsync(Path.Combine(userSkillDirectory, "SKILL.md"),
            "---\nname: user-check\ndescription: User check\n---\nRead.\n");
        await File.WriteAllTextAsync(Path.Combine(projectSkillDirectory, "SKILL.md"),
            "---\nname: project-check\ndescription: Project check\n---\nRead.\n");

        try
        {
            using var response = await RunGetCommandsAsync(root, agentDirectory, ["--local", "--offline", "--no-session", "--approve", "--no-extensions"]);
            var commands = response.RootElement.GetProperty("data").GetProperty("commands").EnumerateArray().ToArray();
            var names = commands.Select(command => command.GetProperty("name").GetString()).ToArray();
            var expected = new[] { "project-review", "user-review", "skill:project-check", "skill:user-check" };
            var positions = expected.Select(name => Array.IndexOf(names, name)).ToArray();
            Assert.All(positions, position => Assert.True(position >= 0));
            Assert.Equal(positions.Order().ToArray(), positions);

            AssertAutoSourceInfo(Assert.Single(commands, command => command.GetProperty("name").GetString() == "project-review")
                .GetProperty("sourceInfo"), Path.Combine(root, ".pi", "prompts", "project-review.md"), "project", Path.Combine(root, ".pi"));
            AssertAutoSourceInfo(Assert.Single(commands, command => command.GetProperty("name").GetString() == "user-review")
                .GetProperty("sourceInfo"), Path.Combine(agentDirectory, "prompts", "user-review.md"), "user", agentDirectory);
            AssertAutoSourceInfo(Assert.Single(commands, command => command.GetProperty("name").GetString() == "skill:project-check")
                .GetProperty("sourceInfo"), Path.Combine(projectSkillDirectory, "SKILL.md"), "project", Path.Combine(root, ".pi"));
            AssertAutoSourceInfo(Assert.Single(commands, command => command.GetProperty("name").GetString() == "skill:user-check")
                .GetProperty("sourceInfo"), Path.Combine(userSkillDirectory, "SKILL.md"), "user", agentDirectory);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<JsonDocument> RunGetCommandsAsync(string workingDirectory, string agentDirectory,
        IReadOnlyList<string> arguments)
    {
        Process? process = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            start.ArgumentList.Add("--mode");
            start.ArgumentList.Add("rpc");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH", "PISHARP_SESSION_DIR" })
                start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"id\":\"commands\",\"type\":\"get_commands\"}");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error.WaitAsync(timeout.Token));
            return JsonDocument.Parse(await output.WaitAsync(timeout.Token));
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process?.Dispose();
        }
    }

    private static void AssertSourceInfo(JsonElement sourceInfo, string path, string baseDirectory)
    {
        Assert.Equal(Path.GetFullPath(path), sourceInfo.GetProperty("path").GetString());
        Assert.Equal("local", sourceInfo.GetProperty("source").GetString());
        Assert.Equal("temporary", sourceInfo.GetProperty("scope").GetString());
        Assert.Equal("top-level", sourceInfo.GetProperty("origin").GetString());
        Assert.Equal(Path.GetFullPath(baseDirectory), sourceInfo.GetProperty("baseDir").GetString());
    }

    private static void AssertAutoSourceInfo(JsonElement sourceInfo, string path, string scope, string baseDirectory)
    {
        Assert.Equal(Path.GetFullPath(path), sourceInfo.GetProperty("path").GetString());
        Assert.Equal("auto", sourceInfo.GetProperty("source").GetString());
        Assert.Equal(scope, sourceInfo.GetProperty("scope").GetString());
        Assert.Equal("top-level", sourceInfo.GetProperty("origin").GetString());
        Assert.Equal(Path.GetFullPath(baseDirectory), sourceInfo.GetProperty("baseDir").GetString());
    }
}
