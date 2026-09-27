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
        Directory.CreateDirectory(privateSkill);
        Directory.CreateDirectory(agent);
        await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"),
            "{\"images\":{\"blockImages\":true},\"sessionDir\":\"custom-sessions\",\"terminal\":{\"trueColor\":true},\"enableSkillCommands\":false}");
        await File.WriteAllTextAsync(Path.Combine(privateSkill, "SKILL.md"),
            "---\nname: private-guide\ndescription: A trusted project guide\n---\nUse this guide.");

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
            var privateSkillResource = Assert.Single(trusted.Resources.Skills, skill => skill.Name == "private-guide");
            Assert.Equal("auto", privateSkillResource.SourceInfo.Source);
            Assert.Equal("project", privateSkillResource.SourceInfo.Scope);
            Assert.Equal(Path.Combine(project, ".pi"), privateSkillResource.SourceInfo.BaseDir);
            Assert.Equal(Path.GetFullPath(Path.Combine(project, "custom-sessions")), trusted.Store.DirectoryPath);

            var untrustedConfiguration = await ProjectRuntimeConfiguration.LoadAsync(project, agent, arguments,
                trust, interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: false);
            using var untrusted = await ProjectRuntimeContext.LoadAsync(untrustedConfiguration, agent, arguments, null);
            Assert.False(untrusted.Trusted);
            Assert.Null(untrusted.Settings.BlockImages);
            Assert.Null(untrusted.Settings.TerminalTrueColor);
            Assert.True(untrusted.Settings.SkillCommandsEnabled);
            Assert.DoesNotContain(untrusted.Resources.Skills, skill => skill.Name == "private-guide");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
