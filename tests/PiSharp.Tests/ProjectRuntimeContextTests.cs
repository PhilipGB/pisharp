using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ProjectRuntimeContextTests
{
    [Fact]
    public async Task LoadsProjectScopedSettingsResourcesAndStoreUnderTheTrustDecision()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-project-runtime-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var privateSkill = Path.Combine(project, ".pi", "skills", "private");
        var configuredSkill = Path.Combine(project, ".pi", "configured-skills", "first");
        var configuredPrompts = Path.Combine(project, ".pi", "configured-prompts");
        Directory.CreateDirectory(privateSkill);
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"), "{\"shellCommandPrefix\":\"export PISHARP_PREFIX=user\"}");
        await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"),
            "{\"images\":{\"blockImages\":true},\"sessionDir\":\"custom-sessions\",\"terminal\":{\"trueColor\":true},\"enableSkillCommands\":false,\"shellCommandPrefix\":\"export PISHARP_PREFIX=project\",\"skills\":[\"configured-skills\"],\"prompts\":[\"configured-prompts\"]}");
        await File.WriteAllTextAsync(Path.Combine(privateSkill, "SKILL.md"),
            "---\nname: private-guide\ndescription: A trusted project guide\n---\nUse this guide.");
        Directory.CreateDirectory(configuredSkill);
        Directory.CreateDirectory(configuredPrompts);
        await File.WriteAllTextAsync(Path.Combine(configuredSkill, "SKILL.md"),
            "---\nname: configured-first\ndescription: A configured project guide\n---\nUse this configured guide.");
        await File.WriteAllTextAsync(Path.Combine(configuredPrompts, "configured-review.md"), "Review configured resources.");

        try
        {
            var trust = new ProjectTrust(agent);
            var arguments = CliArguments.Parse(["--no-extensions"]);
            var trustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: true);
            using var trusted = await ProjectRuntimeContext.LoadAsync(trustedConfiguration, agent, arguments, null);
            Assert.True(trusted.Trusted);
            Assert.True(trusted.Settings.BlockImages);
            Assert.Equal("true", trusted.Settings.TerminalTrueColor);
            Assert.False(trusted.Settings.SkillCommandsEnabled);
            Assert.Equal("export PISHARP_PREFIX=project", trusted.Settings.ShellCommandPrefix);
            var privateSkillResource = Assert.Single(trusted.Resources.Skills, skill => skill.Name == "private-guide");
            Assert.Equal("auto", privateSkillResource.SourceInfo.Source);
            Assert.Equal("project", privateSkillResource.SourceInfo.Scope);
            Assert.Equal(Path.Combine(project, ".pi"), privateSkillResource.SourceInfo.BaseDir);
            Assert.Equal(Path.GetFullPath(Path.Combine(project, "custom-sessions")), trusted.Store.DirectoryPath);
            var configuredResource = Assert.Single(trusted.Resources.Skills, skill => skill.Name == "configured-first");
            Assert.Equal("local", configuredResource.SourceInfo.Source);
            Assert.Equal("project", configuredResource.SourceInfo.Scope);
            Assert.Contains(trusted.Resources.Prompts, prompt => prompt.Name == "configured-review");

            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"), "{\"shellCommandPrefix\":42}");
            var untrustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: false);
            using var untrusted = await ProjectRuntimeContext.LoadAsync(untrustedConfiguration, agent, arguments, null);
            Assert.False(untrusted.Trusted);
            Assert.Null(untrusted.ProjectSettings);
            Assert.Null(untrusted.Settings.BlockImages);
            Assert.Null(untrusted.Settings.TerminalTrueColor);
            Assert.True(untrusted.Settings.SkillCommandsEnabled);
            Assert.Equal("export PISHARP_PREFIX=user", untrusted.Settings.ShellCommandPrefix);
            Assert.DoesNotContain(untrusted.Resources.Skills, skill => skill.Name == "private-guide");
            Assert.DoesNotContain(untrusted.Resources.Skills, skill => skill.Name == "configured-first");
            Assert.DoesNotContain(untrusted.Resources.Prompts, prompt => prompt.Name == "configured-review");
            Assert.Empty(untrusted.Extensions.Registration.ToolDefinitions);

            var explicitBuiltinArguments = CliArguments.Parse(["--no-extensions", "--extension", "builtin:tool-search"]);
            var explicitBuiltinConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent,
                explicitBuiltinArguments, trust, interactiveTrust: false, TextReader.Null, TextWriter.Null,
                trustedOverride: false);
            using (var explicitBuiltin = await ProjectRuntimeContext.LoadAsync(explicitBuiltinConfiguration,
                agent, explicitBuiltinArguments, null))
            {
                Assert.Equal("tool_search", Assert.Single(explicitBuiltin.Extensions.Registration.Tools).Name);
                Assert.Equal("builtin:tool-search", explicitBuiltin.Extensions.Registration
                    .ToolSourceInfo["tool_search"].Path);
            }

            var secondSkill = Path.Combine(project, ".pi", "configured-skills-next", "next");
            Directory.CreateDirectory(secondSkill);
            await File.WriteAllTextAsync(Path.Combine(secondSkill, "SKILL.md"),
                "---\nname: configured-second\ndescription: A reloaded project guide\n---\nUse the reloaded guide.");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"),
                "{\"skills\":[\"configured-skills-next\"]}");
            var reloadedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: true);
            using var reloaded = await ProjectRuntimeContext.LoadAsync(reloadedConfiguration, agent, arguments, null);
            Assert.Contains(reloaded.Resources.Skills, skill => skill.Name == "configured-second");
            Assert.DoesNotContain(reloaded.Resources.Skills, skill => skill.Name == "configured-first");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
