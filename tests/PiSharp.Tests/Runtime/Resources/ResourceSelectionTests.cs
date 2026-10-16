using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceSelectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LinkedFileRootsAreMergedBeforeLoading(bool skill)
    {
        if (!OperatingSystem.IsLinux()) return;
        await WithRootAsync(async root =>
        {
            var first = await WriteAsync(root, "first.md", skill ? Skill("guide") : "first body");
            var alias = Path.Combine(root, "alias.md");
            File.CreateSymbolicLink(alias, first);

            var catalog = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false,
                additionalSkills: skill ? [first, alias] : [], additionalPrompts: skill ? [] : [first, alias]);

            if (skill) Assert.Equal(first, Assert.Single(catalog.Skills).Path);
            else Assert.Equal(first, Assert.Single(catalog.Prompts).Path);
            Assert.Empty(catalog.Diagnostics);
        });
    }

    [Fact]
    public async Task LinkedDirectoryRootKeepsTheFirstAliasName()
    {
        if (!OperatingSystem.IsLinux()) return;
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "real/SKILL.md", "---\ndescription: Guide\n---\nbody");
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), Path.Combine(root, "real"));

            var catalog = await LoadSkillsAsync(root, [Path.Combine(root, "alias"), Path.Combine(root, "real")]);

            Assert.Equal("alias", Assert.Single(catalog.Skills).Name);
            Assert.Empty(catalog.Diagnostics);
        });
    }

    [Fact]
    public async Task SharedSkillAcrossDistinctRootsRetainsBothValidationWarnings()
    {
        if (!OperatingSystem.IsLinux()) return;
        await WithRootAsync(async root =>
        {
            var file = await WriteAsync(root, "a/SKILL.md", Skill("BAD NAME"));
            Directory.CreateDirectory(Path.Combine(root, "b"));
            File.CreateSymbolicLink(Path.Combine(root, "b", "SKILL.md"), file);

            var catalog = await LoadSkillsAsync(root, [Path.Combine(root, "a"), Path.Combine(root, "b")]);

            Assert.Single(catalog.Skills);
            Assert.Equal(["warning", "warning"], catalog.SkillDiagnostics.Select(d => d.Type));
            Assert.Equal([file, Path.Combine(root, "b", "SKILL.md")], catalog.SkillDiagnostics.Select(d => d.Path));
        });
    }

    [Fact]
    public async Task AutomaticFileAndCliDirectoryRetainSeparateValidationWarnings()
    {
        await WithRootAsync(async root =>
        {
            var agent = Path.Combine(root, "agent");
            var path = await WriteAsync(agent, "skills/guide/SKILL.md", Skill("BAD NAME"));

            var catalog = await ResourceCatalog.LoadAsync(root, agent, false,
                additionalSkills: [Path.GetDirectoryName(path)!]);

            Assert.Single(catalog.Skills, s => s.Name == "BAD NAME");
            Assert.Equal(2, catalog.SkillDiagnostics.Count(d => d.Path == path && d.Type == "warning"));
            Assert.DoesNotContain(catalog.SkillDiagnostics, d => d.Type == "collision" && d.Path == path);
        });
    }

    [Fact]
    public async Task SkillWarningsPrecedeNameCollisions()
    {
        await WithRootAsync(async root =>
        {
            var first = await WriteAsync(root, "first.md", Skill("guide"));
            var collision = await WriteAsync(root, "second.md", Skill("guide"));
            var warning = await WriteAsync(root, "third.md", Skill("BAD NAME"));

            var catalog = await LoadSkillsAsync(root, [first, collision, warning]);

            Assert.Equal(["warning", "collision"], catalog.SkillDiagnostics.Select(d => d.Type));
            Assert.Equal([warning, collision], catalog.SkillDiagnostics.Select(d => d.Path));
        });
    }

    [Fact]
    public async Task InvalidLosingPromptIsReadAndReportedAsAWarning()
    {
        await WithRootAsync(async root =>
        {
            var first = await WriteAsync(root, "a/review.md", "first body");
            var invalid = await WriteAsync(root, "b/review.md", "---\ndescription: [broken\n---\nbody");

            var catalog = await LoadPromptsAsync(root, [first, invalid]);

            Assert.Equal(first, Assert.Single(catalog.Prompts).Path);
            var diagnostic = Assert.Single(catalog.PromptDiagnostics);
            Assert.Equal("warning", diagnostic.Type);
            Assert.Equal(invalid, diagnostic.Path);
        });
    }

    [Fact]
    public async Task PromptWarningsPrecedeSlashNamedCollisions()
    {
        await WithRootAsync(async root =>
        {
            var first = await WriteAsync(root, "a/review.md", "first body");
            var collision = await WriteAsync(root, "b/review.md", "second body");
            var invalid = await WriteAsync(root, "c/error.md", "---\ndescription: [broken\n---\nbody");

            var catalog = await LoadPromptsAsync(root, [first, collision, invalid]);

            Assert.Equal(["warning", "collision"], catalog.PromptDiagnostics.Select(d => d.Type));
            Assert.Equal("name \"/review\" collision", catalog.PromptDiagnostics[^1].Message);
        });
    }

    [Fact]
    public async Task ConfiguredSourceDoesNotInventABaseDirectory()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "custom/SKILL.md", Skill("guide"));

            var catalog = await ResourceCatalog.LoadAsync(root, root, false, userSkills: ["custom"]);

            var source = Assert.Single(catalog.Skills, s => s.Name == "guide").SourceInfo;
            Assert.Equal("local", source.Source);
            Assert.Equal("user", source.Scope);
            Assert.Null(source.BaseDir);
        });
    }

    [Fact]
    public async Task CliFileInsideUserDefaultsRetainsAutomaticSourceInfoWhenDefaultsAreDisabled()
    {
        await WithRootAsync(async root =>
        {
            var path = await WriteAsync(root, "skills/guide/SKILL.md", Skill("guide"));

            var catalog = await LoadSkillsAsync(root, [path]);

            var source = Assert.Single(catalog.Skills).SourceInfo;
            Assert.Equal("auto", source.Source);
            Assert.Equal("user", source.Scope);
            Assert.Equal(root, source.BaseDir);
        });
    }

    [Fact]
    public async Task DisabledSkillDefaultsExcludeConfiguredResourcesButKeepCliFiles()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "custom/SKILL.md", Skill("configured"));
            var selected = await WriteAsync(root, "selected.md", Skill("selected"));

            var catalog = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                userSkills: ["custom"], additionalSkills: [selected]);

            Assert.Equal("selected", Assert.Single(catalog.Skills).Name);
        });
    }

    [Fact]
    public async Task MissingSkillWarningKeepsItsRootPosition()
    {
        await WithRootAsync(async root =>
        {
            var first = await WriteAsync(root, "first.md", Skill("BAD NAME"));
            var missing = Path.Combine(root, "missing.md");

            var catalog = await LoadSkillsAsync(root, [first, missing]);

            Assert.Equal([first, missing], catalog.SkillDiagnostics.Select(d => d.Path));
        });
    }

    [Fact]
    public async Task MissingPromptErrorFollowsReadWarningsAndCollisions()
    {
        await WithRootAsync(async root =>
        {
            var first = await WriteAsync(root, "a/review.md", "first body");
            var collision = await WriteAsync(root, "b/review.md", "second body");
            var invalid = await WriteAsync(root, "error.md", "---\ndescription: [broken\n---\nbody");
            var missing = Path.Combine(root, "missing.md");

            var catalog = await LoadPromptsAsync(root, [first, collision, invalid, missing]);

            Assert.Equal(["warning", "collision", "error"], catalog.PromptDiagnostics.Select(d => d.Type));
            Assert.Equal([invalid, collision, missing], catalog.PromptDiagnostics.Select(d => d.Path));
        });
    }

    [Fact]
    public async Task ConfiguredPromptDirectoriesRecurseWithIgnoreRulesAndHiddenFiltering()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "custom/a.md", "a");
            await WriteAsync(root, "custom/child/b.md", "b");
            await WriteAsync(root, "custom/child/.hidden.md", "hidden");
            await WriteAsync(root, "custom/child/blocked.md", "blocked");
            await WriteAsync(root, "custom/.ignore", "child/blocked.md\n");

            var catalog = await ResourceCatalog.LoadAsync(root, root, false, userPrompts: ["custom"]);

            Assert.Equal(["a", "b"], catalog.Prompts.Select(p => p.Name));
        });
    }

    [Fact]
    public async Task AutomaticPromptDirectoriesSkipHiddenAndIgnoredFiles()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "prompts/.hidden.md", "hidden");
            await WriteAsync(root, "prompts/blocked.md", "blocked");
            await WriteAsync(root, "prompts/keep.md", "keep");
            await WriteAsync(root, "prompts/.ignore", "blocked.md\n");

            var catalog = await ResourceCatalog.LoadAsync(root, root, false);

            Assert.Equal("keep", Assert.Single(catalog.Prompts).Name);
        });
    }

    [Fact]
    public async Task AgentsDirectoriesLoadNestedAliasesAndSkipRootAliases()
    {
        await WithRootAsync(async root =>
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            await WriteAsync(root, ".agents/skills/root.md", Skill("fixture-root-alias"));
            await WriteAsync(root, ".agents/skills/pack/guide.md", Skill("fixture-nested-alias"));

            var catalog = await ResourceCatalog.LoadAsync(root, root, true);

            Assert.DoesNotContain(catalog.Skills, s => s.Name == "fixture-root-alias");
            Assert.Single(catalog.Skills, s => s.Name == "fixture-nested-alias");
        });
    }

    [Fact]
    public async Task DirectoryOrderUsesUnicodeScalarsAcrossTheBmpBoundary()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "scan/\ue000.md", Skill("bmp"));
            await WriteAsync(root, "scan/\U00010000.md", Skill("astral"));

            var catalog = await LoadSkillsAsync(root, [Path.Combine(root, "scan")]);

            Assert.Equal(["bmp", "astral"], catalog.Skills.Select(s => s.Name));
        });
    }

    private static Task<ResourceCatalog> LoadSkillsAsync(string root, IReadOnlyList<string> paths) =>
        ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false, additionalSkills: paths);

    private static Task<ResourceCatalog> LoadPromptsAsync(string root, IReadOnlyList<string> paths) =>
        ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false, additionalPrompts: paths);

    private static string Skill(string name) => $"---\nname: {name}\ndescription: Guide\n---\nbody";

    private static async Task<string> WriteAsync(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    private static async Task WithRootAsync(Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
