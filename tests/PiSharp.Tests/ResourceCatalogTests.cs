using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceCatalogTests
{
    [Fact]
    public async Task ProjectResourcesRequireTrustAndSkillsLoadOnDemand()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var userSkill = Path.Combine(agent, "skills", "guide");
        var projectSkill = Path.Combine(project, ".pi", "skills", "secret");
        Directory.CreateDirectory(userSkill);
        Directory.CreateDirectory(projectSkill);
        Directory.CreateDirectory(Path.Combine(agent, "prompts"));
        Directory.CreateDirectory(Path.Combine(project, ".pi", "prompts"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(userSkill, "SKILL.md"), "---\nname: unit-guide\ndescription: A testing guide\n---\nUse references/guide.md");
            await File.WriteAllTextAsync(Path.Combine(projectSkill, "SKILL.md"), "---\nname: project-secret\ndescription: A private guide\ndisable-model-invocation: true\n---\nRun a script");
            await File.WriteAllTextAsync(Path.Combine(agent, "prompts", "review.md"), "---\ndescription: Review changes\n---\nReview $1 and ${2:-security}: $@");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "prompts", "internal.md"), "Internal details");
            var untrusted = await ResourceCatalog.LoadAsync(project, agent, false);
            Assert.DoesNotContain(untrusted.Skills, skill => skill.Name == "project-secret");
            Assert.DoesNotContain(untrusted.Prompts, item => item.Name == "internal");
            Assert.Contains("unit-guide", untrusted.SystemInstructions());
            Assert.DoesNotContain("Use references", untrusted.SystemInstructions());
            Assert.Equal("Review API compatibility and security: API compatibility", untrusted.ExpandPrompt("review", "\"API compatibility\""));
            Assert.Contains("Use references/guide.md", await untrusted.InvokeSkillAsync("unit-guide", "focus"));
            Assert.Equal("Review API compatibility and security: API compatibility", await untrusted.ResolveInputAsync("/review \"API compatibility\""));
            Assert.Contains("Use references/guide.md", await untrusted.ResolveInputAsync("/skill:unit-guide focus"));
            Assert.Equal("ordinary text", await untrusted.ResolveInputAsync("ordinary text"));
            await Assert.ThrowsAsync<ArgumentException>(() => untrusted.ResolveInputAsync("/skill:project-secret"));
            Assert.True(PiSharp.Cli.CliArguments.Parse(["-ns"]).NoSkills);
            Assert.True(PiSharp.Cli.CliArguments.Parse(["--no-skills"]).NoSkills);
            Assert.True(PiSharp.Cli.CliArguments.Parse(["-np"]).NoPromptTemplates);
            Assert.True(PiSharp.Cli.CliArguments.Parse(["--no-prompt-templates"]).NoPromptTemplates);
            var withoutSkills = await ResourceCatalog.LoadAsync(project, agent, true, discoverSkills: false);
            Assert.Empty(withoutSkills.Skills);
            Assert.Empty(withoutSkills.SystemInstructions());
            Assert.NotEmpty(withoutSkills.Prompts);
            await Assert.ThrowsAsync<ArgumentException>(() => withoutSkills.ResolveInputAsync("/skill:unit-guide"));
            var withoutPrompts = await ResourceCatalog.LoadAsync(project, agent, true, discoverPrompts: false);
            Assert.Empty(withoutPrompts.Prompts);
            Assert.Equal("/review x", await withoutPrompts.ResolveInputAsync("/review x"));
            Assert.NotEmpty(withoutPrompts.Skills);
            var explicitFlags = PiSharp.Cli.CliArguments.Parse(["--no-skills", "--no-prompt-templates",
                "--skill", Path.Combine(projectSkill, "SKILL.md"), "--prompt-template", Path.Combine(agent, "prompts", "review.md")]);
            var explicitResources = await ResourceCatalog.LoadAsync(project, agent, false,
                discoverSkills: !explicitFlags.NoSkills, discoverPrompts: !explicitFlags.NoPromptTemplates,
                additionalSkills: explicitFlags.SkillPaths, additionalPrompts: explicitFlags.PromptTemplatePaths);
            Assert.Single(explicitResources.Skills);
            Assert.Equal("project-secret", explicitResources.Skills[0].Name);
            Assert.Single(explicitResources.Prompts);
            Assert.Contains("Review value", await explicitResources.ResolveInputAsync("/review value"));
            Assert.Equal("Run a script", (await explicitResources.InvokeSkillAsync("project-secret", "")).Split('\n')[1]);
            var collidingSkill = Path.Combine(agent, "skills", "collision");
            Directory.CreateDirectory(collidingSkill);
            await File.WriteAllTextAsync(Path.Combine(collidingSkill, "SKILL.md"),
                "---\nname: project-secret\ndescription: Auto discovered collision\n---\nWrong skill");
            var chosenPrompt = Path.Combine(project, ".pi", "prompts", "review.md");
            await File.WriteAllTextAsync(chosenPrompt, "Chosen prompt: $1");
            var chosen = await ResourceCatalog.LoadAsync(project, agent, false,
                additionalSkills: [Path.Combine(projectSkill, "SKILL.md")],
                additionalPrompts: [chosenPrompt]);
            Assert.Equal("Chosen prompt: value", chosen.ExpandPrompt("review", "value"));
            Assert.Contains("Run a script", await chosen.InvokeSkillAsync("project-secret", ""));
            Assert.DoesNotContain("Wrong skill", await chosen.InvokeSkillAsync("project-secret", ""));
            File.Delete(Path.Combine(collidingSkill, "SKILL.md"));
            await Assert.ThrowsAsync<FileNotFoundException>(() => ResourceCatalog.LoadAsync(project, agent, false,
                discoverSkills: false, additionalSkills: ["missing-skill"]));
            Assert.Throws<ArgumentException>(() => PiSharp.Cli.CliArguments.Parse(["--skill"]));
            var trusted = await ResourceCatalog.LoadAsync(project, agent, true);
            Assert.Contains(trusted.Skills, skill => skill.Name == "project-secret");
            Assert.DoesNotContain("project-secret", trusted.SystemInstructions());
            Assert.Contains("Run a script", await trusted.InvokeSkillAsync("project-secret", ""));
            Assert.Contains(trusted.Prompts, item => item.Name == "internal");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OversizedAndInvalidUtf8ResourcesFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(root, "prompts");
        Directory.CreateDirectory(prompts);
        var path = Path.Combine(prompts, "oversized.md");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[64 * 1024 + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => ResourceCatalog.LoadAsync(root, root, false));
            var ignored = await ResourceCatalog.LoadAsync(root, root, false, discoverPrompts: false);
            Assert.Empty(ignored.Prompts);
            await File.WriteAllBytesAsync(path, [0xC3, 0x28]);
            await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(() => ResourceCatalog.LoadAsync(root, root, false));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidResourcesDoNotAdvertiseAndBadArgumentsFail()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "skills", "bad");
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), "---\nname: Bad Name\ndescription: invalid\n---\ntext");
            var resources = await ResourceCatalog.LoadAsync(root, root, false);
            Assert.DoesNotContain(resources.Skills, skill => skill.Name == "Bad Name");
            Assert.Throws<ArgumentException>(() => resources.ExpandPrompt("absent", ""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
