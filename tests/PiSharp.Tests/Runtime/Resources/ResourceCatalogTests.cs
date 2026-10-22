using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
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
    public async Task PromptTemplateFrontmatterChompsBodyWhitespaceLikePi()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-prompt-chomping-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(root, "prompts");
        Directory.CreateDirectory(prompts);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(prompts, "review.md"),
                "---\ndescription: Review\n---\nReview $1\n\n");
            await File.WriteAllTextAsync(Path.Combine(prompts, "plain.md"), "Plain body\n\n");

            var resources = await ResourceCatalog.LoadAsync(root, root, trusted: false);

            Assert.Equal("Review value", resources.ExpandPrompt("review", "value"));
            Assert.Equal("Plain body\n\n", resources.ExpandPrompt("plain", ""));
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
            Assert.Equal(
                $"<skill name=\"unit-guide\" location=\"{Path.Combine(userSkill, "SKILL.md")}\">\n" +
                $"References are relative to {userSkill}.\n\nUse references/guide.md\n</skill>\n\nfocus",
                await untrusted.InvokeSkillAsync("unit-guide", "focus"));
            var userPromptSource = Assert.Single(untrusted.Prompts).SourceInfo;
            Assert.Equal("user", userPromptSource.Scope);
            Assert.Equal(agent, userPromptSource.BaseDir);
            Assert.Contains("unit-guide", untrusted.SystemInstructions());
            Assert.Contains("Use the read tool to load a skill's file", untrusted.SystemInstructions());
            Assert.Contains("<location>" + Path.Combine(userSkill, "SKILL.md") + "</location>",
                untrusted.SystemInstructions());
            Assert.Contains("Use bash to load a skill's file", untrusted.SystemInstructions("bash"));
            Assert.Contains("Load a skill's file when the task matches its description.",
                untrusted.SystemInstructions("indirect"));
            Assert.DoesNotContain("Use the read tool to load", untrusted.SystemInstructions("indirect"));
            Assert.DoesNotContain("Use bash to load", untrusted.SystemInstructions("indirect"));
            Assert.Empty(untrusted.SystemInstructions(null));
            Assert.Empty(untrusted.SystemInstructions("write"));
            Assert.DoesNotContain("Use references", untrusted.SystemInstructions());
            Assert.Equal("Review API compatibility and security: API compatibility", untrusted.ExpandPrompt("review", "\"API compatibility\""));
            Assert.Contains("Use references/guide.md", await untrusted.InvokeSkillAsync("unit-guide", "focus"));
            Assert.Equal("Review API compatibility and security: API compatibility", await untrusted.ResolveInputAsync("/review \"API compatibility\""));
            Assert.Contains("Use references/guide.md", await untrusted.ResolveInputAsync("/skill:unit-guide focus"));
            Assert.Equal("ordinary text", await untrusted.ResolveInputAsync("ordinary text"));
            Assert.Equal("/skill:project-secret", await untrusted.ResolveInputAsync("/skill:project-secret"));
            await Assert.ThrowsAsync<ArgumentException>(() => untrusted.InvokeSkillAsync("project-secret", ""));
            Assert.True(PiSharp.Cli.CliArguments.Parse(["-ns"]).NoSkills);
            Assert.True(PiSharp.Cli.CliArguments.Parse(["--no-skills"]).NoSkills);
            Assert.True(PiSharp.Cli.CliArguments.Parse(["-np"]).NoPromptTemplates);
            Assert.True(PiSharp.Cli.CliArguments.Parse(["--no-prompt-templates"]).NoPromptTemplates);
            var withoutSkills = await ResourceCatalog.LoadAsync(project, agent, true, discoverSkills: false);
            Assert.Empty(withoutSkills.Skills);
            Assert.Empty(withoutSkills.SystemInstructions());
            Assert.NotEmpty(withoutSkills.Prompts);
            Assert.Equal("/skill:unit-guide", await withoutSkills.ResolveInputAsync("/skill:unit-guide"));
            await Assert.ThrowsAsync<ArgumentException>(() => withoutSkills.InvokeSkillAsync("unit-guide", ""));
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
            Assert.Equal("project", explicitResources.Skills[0].SourceInfo.Scope);
            Assert.Equal("user", explicitResources.Prompts[0].SourceInfo.Scope);
            Assert.Contains("Review value", await explicitResources.ResolveInputAsync("/review value"));
            Assert.Equal(
                $"<skill name=\"project-secret\" location=\"{Path.Combine(projectSkill, "SKILL.md")}\">\n" +
                $"References are relative to {projectSkill}.\n\nRun a script\n</skill>",
                await explicitResources.InvokeSkillAsync("project-secret", ""));
            var collidingSkill = Path.Combine(agent, "skills", "collision");
            Directory.CreateDirectory(collidingSkill);
            await File.WriteAllTextAsync(Path.Combine(collidingSkill, "SKILL.md"),
                "---\nname: project-secret\ndescription: Auto discovered collision\n---\nWrong skill");
            var chosenPrompt = Path.Combine(project, ".pi", "prompts", "review.md");
            await File.WriteAllTextAsync(chosenPrompt, "Chosen prompt: $1");
            var chosen = await ResourceCatalog.LoadAsync(project, agent, false,
                additionalSkills: [Path.Combine(projectSkill, "SKILL.md")],
                additionalPrompts: [chosenPrompt]);
            Assert.Equal("Review value and security: value", chosen.ExpandPrompt("review", "value"));
            Assert.Equal(Path.Combine(agent, "prompts", "review.md"),
                Assert.Single(chosen.Prompts, prompt => prompt.Name == "review").Path);
            Assert.Contains(chosen.Diagnostics, diagnostic => diagnostic.Collision?.Name == "review" &&
                diagnostic.Collision.WinnerPath == Path.Combine(agent, "prompts", "review.md") &&
                diagnostic.Collision.LoserPath == chosenPrompt);
            Assert.Contains("Wrong skill", await chosen.InvokeSkillAsync("project-secret", ""));
            Assert.DoesNotContain("Run a script", await chosen.InvokeSkillAsync("project-secret", ""));
            File.Delete(Path.Combine(collidingSkill, "SKILL.md"));
            var missing = await ResourceCatalog.LoadAsync(project, agent, false,
                discoverSkills: false, additionalSkills: ["missing-skill"]);
            Assert.Contains(missing.SkillDiagnostics, diagnostic => diagnostic.Message == "skill path does not exist" &&
                diagnostic.Path == Path.Combine(project, "missing-skill"));
            Assert.Throws<ArgumentException>(() => PiSharp.Cli.CliArguments.Parse(["--skill"]));
            var trusted = await ResourceCatalog.LoadAsync(project, agent, true);
            Assert.Contains(trusted.Skills, skill => skill.Name == "project-secret");
            var projectSkillSource = Assert.Single(trusted.Skills, skill => skill.Name == "project-secret").SourceInfo;
            Assert.Equal("auto", projectSkillSource.Source);
            Assert.Equal("project", projectSkillSource.Scope);
            Assert.Equal(Path.Combine(project, ".pi"), projectSkillSource.BaseDir);
            Assert.DoesNotContain("project-secret", trusted.SystemInstructions());
            Assert.Equal(
                $"<skill name=\"project-secret\" location=\"{Path.Combine(projectSkill, "SKILL.md")}\">\n" +
                $"References are relative to {projectSkill}.\n\nRun a script\n</skill>",
                await trusted.InvokeSkillAsync("project-secret", ""));
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
            await WriteSkill(Path.Combine(agent, "skills", "blocked", "9keep"), "user-auto-numeric");
            await WriteSkill(Path.Combine(project, ".pi", "custom-skills", "project"), "project-configured");
            await WriteSkill(Path.Combine(project, ".pi", "skills", "blocked", "skip"), "project-auto-skip");
            Directory.CreateDirectory(Path.Combine(agent, "custom-prompts"));
            Directory.CreateDirectory(Path.Combine(agent, "prompts"));
            Directory.CreateDirectory(Path.Combine(project, ".pi", "custom-prompts"));
            await File.WriteAllTextAsync(Path.Combine(agent, "custom-prompts", "user-review.md"), "User prompt");
            await File.WriteAllTextAsync(Path.Combine(agent, "prompts", "blocked.md"), "Hidden auto prompt");
            await File.WriteAllTextAsync(Path.Combine(agent, "prompts", "another-blocked.md"), "Hidden auto prompt");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "custom-prompts", "project-review.md"), "Project prompt");

            IReadOnlyList<string> userSkills = ["custom-skills", "+skills/blocked/keep/SKILL.md",
                "!skills/blocked/[[:alpha:]]*/SKILL.md", "-custom-skills/disabled/SKILL.md"];
            IReadOnlyList<string> projectSkills = ["custom-skills", "!skills/blocked/**"];
            IReadOnlyList<string> userPrompts = ["custom-prompts", "!prompts/{blocked,another-blocked}.md"];
            IReadOnlyList<string> projectPrompts = ["custom-prompts"];

            var untrusted = await ResourceCatalog.LoadAsync(project, agent, false,
                userSkills: userSkills, projectSkills: projectSkills, userPrompts: userPrompts, projectPrompts: projectPrompts);
            Assert.Contains(untrusted.Skills, skill => skill.Name == "user-configured");
            Assert.Contains(untrusted.Skills, skill => skill.Name == "user-auto-keep");
            Assert.Contains(untrusted.Skills, skill => skill.Name == "user-auto-numeric");
            Assert.DoesNotContain(untrusted.Skills, skill => skill.Name is "user-disabled" or "user-auto-skip" or "project-configured" or "project-auto-skip");
            Assert.Contains(untrusted.Prompts, prompt => prompt.Name == "user-review");
            Assert.DoesNotContain(untrusted.Prompts, prompt =>
                prompt.Name is "blocked" or "another-blocked" or "project-review");
            var userSource = Assert.Single(untrusted.Skills, skill => skill.Name == "user-configured").SourceInfo;
            Assert.Equal("local", userSource.Source);
            Assert.Equal("user", userSource.Scope);
            Assert.Null(userSource.BaseDir);
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
            Assert.Null(projectSource.BaseDir);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OversizedAndInvalidUtf8ResourcesPreserveCompleteDecodedText()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var prompts = Path.Combine(root, "prompts");
        Directory.CreateDirectory(prompts);
        var path = Path.Combine(prompts, "oversized.md");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[64 * 1024 + 1]);
            var large = await ResourceCatalog.LoadAsync(root, root, false);
            Assert.Equal(new string('\0', 64 * 1024 + 1), Assert.Single(large.Prompts).Template);
            var ignored = await ResourceCatalog.LoadAsync(root, root, false, discoverPrompts: false);
            Assert.Empty(ignored.Prompts);
            await File.WriteAllBytesAsync(path, [0xC3, 0x28]);
            var replaced = await ResourceCatalog.LoadAsync(root, root, false);
            Assert.Equal("\uFFFD(", Assert.Single(replaced.Prompts).Template);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WarnOnlyInvalidSkillNamesRemainLoadedAndBadArgumentsFail()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "skills", "unsafe&skill");
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"),
                "---\nname: unsafe&skill\ndescription: >-\n  A <safe> & \"quoted\" 'single'\n---\ntext");
            var resources = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                additionalSkills: [Path.Combine(root, "skills")]);
            var skill = Assert.Single(resources.Skills);
            Assert.Equal("unsafe&skill", skill.Name);
            Assert.Equal("A <safe> & \"quoted\" 'single'", skill.Description);
            Assert.Contains(resources.Diagnostics, diagnostic => diagnostic.Type == "warning" &&
                diagnostic.Message.Contains("invalid characters", StringComparison.Ordinal));
            var instructions = resources.SystemInstructions();
            Assert.Contains("<name>unsafe&amp;skill</name>", instructions);
            Assert.Contains("<description>A &lt;safe&gt; &amp; &quot;quoted&quot; &apos;single&apos;</description>", instructions);
            Assert.Contains($"<location>{Path.Combine(folder, "SKILL.md").Replace("&", "&amp;", StringComparison.Ordinal)}</location>",
                instructions);
            Assert.Contains($"<skill name=\"unsafe&skill\" location=\"{Path.Combine(folder, "SKILL.md")}\">",
                await resources.InvokeSkillAsync("unsafe&skill", ""));
            Assert.Throws<ArgumentException>(() => resources.ExpandPrompt("absent", ""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SkillFallbackNamesAndLongDescriptionsRemainLoadedWithWarnings()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var fallback = Path.Combine(root, "skills", "folder-name", "SKILL.md");
        var longDescription = Path.Combine(root, "skills", "long-description", "SKILL.md");
        var missingDescription = Path.Combine(root, "skills", "missing-description", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
        Directory.CreateDirectory(Path.GetDirectoryName(longDescription)!);
        Directory.CreateDirectory(Path.GetDirectoryName(missingDescription)!);
        try
        {
            await File.WriteAllTextAsync(fallback, "---\ndescription: folder fallback\n---\nbody");
            await File.WriteAllTextAsync(longDescription,
                $"---\nname: long-description\ndescription: {new string('x', 1025)}\n---\nbody");
            await File.WriteAllTextAsync(missingDescription, "---\nname: missing-description\ndescription:  \n---\nbody");

            var resources = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                additionalSkills: [Path.Combine(root, "skills")]);

            Assert.Contains(resources.Skills, skill => skill.Name == "folder-name");
            Assert.Contains(resources.Skills, skill => skill.Name == "long-description");
            Assert.DoesNotContain(resources.Skills, skill => skill.Name == "missing-description");
            Assert.Contains(resources.Diagnostics, diagnostic => diagnostic.Path == longDescription &&
                diagnostic.Message.Contains("exceeds 1024 characters", StringComparison.Ordinal));
            Assert.Contains(resources.Diagnostics, diagnostic => diagnostic.Path == missingDescription &&
                diagnostic.Message == "description is required");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SkillMetadataWarningsAppearInExpandedStartupDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-diagnostics-" + Guid.NewGuid().ToString("N"));
        var skillPath = Path.Combine(root, "skills", "unsafe&skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        try
        {
            await File.WriteAllTextAsync(skillPath,
                "---\nname: unsafe&skill\ndescription: loaded with a warning\n---\nbody");
            var resources = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                additionalSkills: [Path.Combine(root, "skills")]);
            using var extensions = ExtensionCatalog.Load(root, root, projectTrusted: false, discover: false,
                builtins: []);

            var details = TerminalStartupDetails.Build([], resources, extensions, root);
            var warning = Assert.Single(details, detail => detail.Name == "Skill conflicts");

            Assert.Contains(warning.Items, item => item.Contains("invalid characters", StringComparison.Ordinal));
            var displayPath = TerminalStartupDetails.AbbreviateHome(skillPath,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Assert.Contains(warning.ExpandedItems!, item => item.Contains(displayPath, StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExpandedStartupResourceListsGroupPathsByTheirPiScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-startup-scopes-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var projectSkill = Path.Combine(project, ".pi", "skills", "project-skill", "SKILL.md");
        var userSkill = Path.Combine(agent, "skills", "user-skill", "SKILL.md");
        var projectPrompt = Path.Combine(project, ".pi", "prompts", "project-prompt.md");
        var userPrompt = Path.Combine(agent, "prompts", "user-prompt.md");
        try
        {
            async Task WriteAsync(string path, string content)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content);
            }

            await WriteAsync(projectSkill,
                "---\nname: project-skill\ndescription: project\n---\nProject skill.");
            await WriteAsync(userSkill,
                "---\nname: user-skill\ndescription: user\n---\nUser skill.");
            await WriteAsync(projectPrompt, "Project prompt.");
            await WriteAsync(userPrompt, "User prompt.");

            var resources = await ResourceCatalog.LoadAsync(project, agent, trusted: true,
                discoverSkills: false, discoverPrompts: false,
                additionalSkills: [projectSkill, userSkill], additionalPrompts: [projectPrompt, userPrompt],
                userSkills: [Path.Combine(agent, "skills")],
                projectSkills: [Path.Combine(project, ".pi", "skills")],
                userPrompts: [Path.Combine(agent, "prompts")],
                projectPrompts: [Path.Combine(project, ".pi", "prompts")]);
            using var extensions = ExtensionCatalog.Load(project, agent, projectTrusted: true,
                discover: false, builtins: []);
            var details = TerminalStartupDetails.Build([], resources, extensions, project);
            var presentation = new TerminalStartupPresentation(null, verbose: false);
            var rendered = presentation.BuildDetails(TerminalTheme.Default, 120, details,
                projectTrustNotice: null, expanded: true);
            var lines = rendered.Split('\n').Select(StripAnsi).ToArray();

            Assert.Contains("[Skills]", lines);
            Assert.Contains("[Prompts]", lines);
            Assert.Contains("  project", lines);
            Assert.Contains("  user", lines);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Contains("    " + TerminalStartupDetails.AbbreviateHome(projectSkill, home), lines);
            Assert.Contains("    " + TerminalStartupDetails.AbbreviateHome(userSkill, home), lines);
            Assert.Contains("    /project-prompt", lines);
            Assert.Contains("    /user-prompt", lines);

            var compact = presentation.BuildDetails(TerminalTheme.Default, 120, details,
                projectTrustNotice: null, expanded: false).Split('\n').Select(StripAnsi).ToArray();
            Assert.Contains("[Skills]", compact);
            Assert.Contains("  project-skill, user-skill", compact);
            Assert.Contains("  /project-prompt, /user-prompt", compact);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StartupPathsKeepTheSeparatorAfterHomeAbbreviation()
    {
        var home = Path.Combine(Path.GetTempPath(), "pisharp-home");
        var path = Path.Combine(home, ".agents", "skills", "startup-skill", "SKILL.md");

        Assert.Equal("~" + Path.DirectorySeparatorChar + ".agents" + Path.DirectorySeparatorChar +
            "skills" + Path.DirectorySeparatorChar + "startup-skill" + Path.DirectorySeparatorChar + "SKILL.md",
            TerminalStartupDetails.AbbreviateHome(path, home));
    }

    private static string StripAnsi(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "\\u001b\\[[0-?]*[ -/]*[@-~]", "");

    [Fact]
    public async Task SkillPromptPreservesPiProjectUserAndTemporaryPrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-skill-order-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var temporary = Path.Combine(root, "temporary", "temporary-skill", "SKILL.md");
        try
        {
            async Task WriteSkill(string path, string name)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, $"---\nname: {name}\ndescription: {name} description\n---\n{name} body");
            }

            await WriteSkill(Path.Combine(project, ".pi", "custom-skills", "project-skill", "SKILL.md"), "project-skill");
            await WriteSkill(Path.Combine(agent, "custom-skills", "user-skill", "SKILL.md"), "user-skill");
            await WriteSkill(temporary, "temporary-skill");
            var projectShared = Path.Combine(project, ".pi", "custom-skills", "project-shared", "SKILL.md");
            var userShared = Path.Combine(agent, "custom-skills", "user-shared", "SKILL.md");
            var temporaryShared = Path.Combine(root, "temporary", "shared", "SKILL.md");
            await WriteSkill(projectShared, "shared");
            await WriteSkill(userShared, "shared");
            await WriteSkill(temporaryShared, "shared");

            var resources = await ResourceCatalog.LoadAsync(project, agent, trusted: true,
                additionalSkills: [Path.Combine(root, "temporary")], userSkills: ["custom-skills"],
                projectSkills: ["custom-skills"]);

            Assert.Equal("project", Assert.Single(resources.Skills, skill => skill.Name == "project-skill").SourceInfo.Scope);
            Assert.Equal("user", Assert.Single(resources.Skills, skill => skill.Name == "user-skill").SourceInfo.Scope);
            Assert.Equal("temporary", Assert.Single(resources.Skills, skill => skill.Name == "temporary-skill").SourceInfo.Scope);
            Assert.Equal(projectShared, Assert.Single(resources.Skills, skill => skill.Name == "shared").Path);
            var sharedCollisions = resources.Diagnostics.Where(diagnostic => diagnostic.Collision?.Name == "shared").ToArray();
            Assert.Equal(2, sharedCollisions.Length);
            Assert.All(sharedCollisions, diagnostic => Assert.Equal(projectShared, diagnostic.Collision!.WinnerPath));
            Assert.Contains(sharedCollisions, diagnostic => diagnostic.Collision!.LoserPath == userShared);
            Assert.Contains(sharedCollisions, diagnostic => diagnostic.Collision!.LoserPath == temporaryShared);
            var instructions = resources.SystemInstructions();
            var projectIndex = instructions.IndexOf("<name>project-skill</name>", StringComparison.Ordinal);
            var userIndex = instructions.IndexOf("<name>user-skill</name>", StringComparison.Ordinal);
            var temporaryIndex = instructions.IndexOf("<name>temporary-skill</name>", StringComparison.Ordinal);
            Assert.True(projectIndex >= 0 && projectIndex < userIndex && userIndex < temporaryIndex,
                instructions);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
