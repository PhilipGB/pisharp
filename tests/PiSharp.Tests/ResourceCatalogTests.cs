using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceCatalogTests
{
    [Theory]
    [InlineData(">", "A testing guide with a second line.\n")]
    [InlineData("|", "A testing guide\nwith a second line.\n")]
    public async Task SkillDescriptionsHonorYamlBlockScalarStyles(string scalarStyle, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-block-scalar-" + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(root, "skills", "block-guide");
        Directory.CreateDirectory(skillDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"),
                $"---\nname: block-guide\ndescription: {scalarStyle}\n  A testing guide\n  with a second line.\n---\nUse the guide.");

            var resources = await ResourceCatalog.LoadAsync(root, root, trusted: false);

            Assert.Equal(expected, Assert.Single(resources.Skills, skill => skill.Name == "block-guide").Description);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
            var userSkillSource = Assert.Single(untrusted.Skills, skill => skill.Name == "unit-guide").SourceInfo;
            Assert.Equal(Path.Combine(userSkill, "SKILL.md"), userSkillSource.Path);
            Assert.Equal("auto", userSkillSource.Source);
            Assert.Equal("user", userSkillSource.Scope);
            Assert.Equal(agent, userSkillSource.BaseDir);
            var userPromptSource = Assert.Single(untrusted.Prompts).SourceInfo;
            Assert.Equal("user", userPromptSource.Scope);
            Assert.Equal(agent, userPromptSource.BaseDir);
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
            Assert.Equal("temporary", explicitResources.Skills[0].SourceInfo.Scope);
            Assert.Equal("temporary", explicitResources.Prompts[0].SourceInfo.Scope);
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
            var projectSkillSource = Assert.Single(trusted.Skills, skill => skill.Name == "project-secret").SourceInfo;
            Assert.Equal("auto", projectSkillSource.Source);
            Assert.Equal("project", projectSkillSource.Scope);
            Assert.Equal(Path.Combine(project, ".pi"), projectSkillSource.BaseDir);
            Assert.DoesNotContain("project-secret", trusted.SystemInstructions());
            Assert.Contains("Run a script", await trusted.InvokeSkillAsync("project-secret", ""));
            Assert.Contains(trusted.Prompts, item => item.Name == "internal");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConfiguredResourcePathsUseScopedBasesAndTrustWhilePatternsFilterAutoDiscovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-configured-resources-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        try
        {
            async Task WriteSkill(string directory, string name)
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"),
                    $"---\nname: {name}\ndescription: {name} description\n---\n{name} body");
            }

            await WriteSkill(Path.Combine(agent, "custom-skills", "active"), "user-configured");
            await WriteSkill(Path.Combine(agent, "custom-skills", "disabled"), "user-disabled");
            await WriteSkill(Path.Combine(agent, "skills", "blocked", "skip"), "user-auto-skip");
            await WriteSkill(Path.Combine(agent, "skills", "blocked", "keep"), "user-auto-keep");
            await WriteSkill(Path.Combine(project, ".pi", "custom-skills", "project"), "project-configured");
            await WriteSkill(Path.Combine(project, ".pi", "skills", "blocked", "skip"), "project-auto-skip");
            Directory.CreateDirectory(Path.Combine(agent, "custom-prompts"));
            Directory.CreateDirectory(Path.Combine(agent, "prompts"));
            Directory.CreateDirectory(Path.Combine(project, ".pi", "custom-prompts"));
            await File.WriteAllTextAsync(Path.Combine(agent, "custom-prompts", "user-review.md"), "User prompt");
            await File.WriteAllTextAsync(Path.Combine(agent, "prompts", "blocked.md"), "Hidden auto prompt");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "custom-prompts", "project-review.md"), "Project prompt");

            IReadOnlyList<string> userSkills = ["custom-skills", "+skills/blocked/keep/SKILL.md",
                "!skills/blocked/**", "-custom-skills/disabled/SKILL.md"];
            IReadOnlyList<string> projectSkills = ["custom-skills", "!skills/blocked/**"];
            IReadOnlyList<string> userPrompts = ["custom-prompts", "!prompts/blocked.md"];
            IReadOnlyList<string> projectPrompts = ["custom-prompts"];

            var untrusted = await ResourceCatalog.LoadAsync(project, agent, false,
                userSkills: userSkills, projectSkills: projectSkills, userPrompts: userPrompts, projectPrompts: projectPrompts);
            Assert.Contains(untrusted.Skills, skill => skill.Name == "user-configured");
            Assert.Contains(untrusted.Skills, skill => skill.Name == "user-auto-keep");
            Assert.DoesNotContain(untrusted.Skills, skill => skill.Name is "user-disabled" or "user-auto-skip" or "project-configured" or "project-auto-skip");
            Assert.Contains(untrusted.Prompts, prompt => prompt.Name == "user-review");
            Assert.DoesNotContain(untrusted.Prompts, prompt => prompt.Name is "blocked" or "project-review");
            var userSource = Assert.Single(untrusted.Skills, skill => skill.Name == "user-configured").SourceInfo;
            Assert.Equal("local", userSource.Source);
            Assert.Equal("user", userSource.Scope);
            Assert.Equal(Path.GetFullPath(agent), userSource.BaseDir);
            var autoSource = Assert.Single(untrusted.Skills, skill => skill.Name == "user-auto-keep").SourceInfo;
            Assert.Equal("auto", autoSource.Source);

            var trusted = await ResourceCatalog.LoadAsync(project, agent, true,
                userSkills: userSkills, projectSkills: projectSkills, userPrompts: userPrompts, projectPrompts: projectPrompts);
            Assert.Contains(trusted.Skills, skill => skill.Name == "project-configured");
            Assert.DoesNotContain(trusted.Skills, skill => skill.Name == "project-auto-skip");
            Assert.Contains(trusted.Prompts, prompt => prompt.Name == "project-review");
            var projectSource = Assert.Single(trusted.Skills, skill => skill.Name == "project-configured").SourceInfo;
            Assert.Equal("local", projectSource.Source);
            Assert.Equal("project", projectSource.Scope);
            Assert.Equal(Path.Combine(Path.GetFullPath(project), ".pi"), projectSource.BaseDir);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
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
