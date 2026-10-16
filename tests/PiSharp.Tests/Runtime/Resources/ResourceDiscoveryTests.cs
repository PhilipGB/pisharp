using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceDiscoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeclaredRootStopsDiscoveryEvenWhenItsDescriptionIsMissing(bool valid)
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "SKILL.md", Skill("root", valid));
            await WriteAsync(root, "alias.md", Skill("alias"));
            await WriteAsync(root, "child/SKILL.md", Skill("child"));

            var resources = await LoadAsync(root);

            Assert.Equal(valid ? ["root"] : Array.Empty<string>(), resources.Skills.Select(s => s.Name));
            Assert.Equal(valid ? 0 : 1, resources.SkillDiagnostics.Count);
        });
    }

    [Fact]
    public async Task InvalidDeclaredRootDoesNotFallThroughToChildSkills()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "SKILL.md", "---\ndescription: [broken\n---\nbody");
            await WriteAsync(root, "child/SKILL.md", Skill("child"));

            var resources = await LoadAsync(root);

            Assert.Empty(resources.Skills);
            Assert.Single(resources.SkillDiagnostics);
        });
    }

    [Fact]
    public async Task RootAliasesInterleaveWithNestedDeclarationsInDirectoryOrder()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "a.md", Skill("root-alias"));
            await WriteAsync(root, "a/SKILL.md", Skill("nested"));
            await WriteAsync(root, "z/alias.md", Skill("nested-alias"));

            var resources = await LoadAsync(root);

            Assert.Equal(["nested", "root-alias"], resources.Skills.Select(s => s.Name));
        });
    }

    [Fact]
    public async Task SkillDeclarationAndMarkdownExtensionsAreCaseSensitive()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "skill.md", Skill("alias"));
            await WriteAsync(root, "SKILL.MD", Skill("uppercase"));
            await WriteAsync(root, "child/skill.md", Skill("nested-alias"));

            var resources = await LoadAsync(root);

            Assert.Equal("alias", Assert.Single(resources.Skills).Name);
        });
    }

    [Fact]
    public async Task PromptDirectoriesIncludeHiddenMarkdownButDoNotRecurse()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, ".hidden.md", "hidden body");
            await WriteAsync(root, "upper.MD", "upper body");
            await WriteAsync(root, "child/nested.md", "nested body");

            var resources = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                discoverPrompts: false, additionalPrompts: [root], userPrompts: [root]);

            Assert.Equal(".hidden", Assert.Single(resources.Prompts).Name);
        });
    }

    [Fact]
    public async Task SkillDiscoveryDoesNotTruncateAtOneThousandFiles()
    {
        await WithRootAsync(async root =>
        {
            for (var index = 0; index < 1002; index++)
                await WriteAsync(root, $"s{index:0000}/SKILL.md", Skill($"s{index:0000}"));

            var resources = await LoadAsync(root);

            Assert.Equal(1002, resources.Skills.Count);
            Assert.Equal("s1001", resources.Skills[^1].Name);
        });
    }

    [Fact]
    public async Task SkillDiscoveryFollowsDirectoryLinksAndStopsAncestorCycles()
    {
        if (!OperatingSystem.IsLinux()) return;
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "target/SKILL.md", Skill("linked"));
            var scan = Path.Combine(root, "scan");
            Directory.CreateDirectory(scan);
            Directory.CreateSymbolicLink(Path.Combine(scan, "linked"), Path.Combine(root, "target"));
            Directory.CreateSymbolicLink(Path.Combine(scan, "cycle"), scan);

            var resources = await LoadAsync(scan);

            Assert.Equal("linked", Assert.Single(resources.Skills).Name);
        });
    }

    private static Task<ResourceCatalog> LoadAsync(string root) => ResourceCatalog.LoadAsync(root, root, false,
        discoverSkills: false, discoverPrompts: false, additionalSkills: [root]);

    private static string Skill(string name, bool description = true) =>
        $"---\nname: {name}\n" + (description ? $"description: {name} guide\n" : "") + $"---\n{name} body";

    private static async Task WriteAsync(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
    }

    private static async Task WithRootAsync(Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
