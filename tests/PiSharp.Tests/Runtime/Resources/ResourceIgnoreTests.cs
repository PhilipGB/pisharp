using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceIgnoreTests
{
    [Theory]
    [InlineData("[[:alpha:]]*.md", "digit")]
    [InlineData("[[:digit:]]*.md", "alpha")]
    [InlineData("[![:digit:]]*.md", "digit")]
    [InlineData("[]a].md", "digit")]
    public async Task CharacterClassesFilterTheMatchingRootAliases(string pattern, string remaining)
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, ".ignore", pattern + "\n");
            await WriteAsync(root, "a.md", Skill("alpha"));
            await WriteAsync(root, "1.md", Skill("digit"));

            var resources = await LoadAsync(root);

            Assert.Equal(remaining, Assert.Single(resources.Skills).Name);
        });
    }

    [Theory]
    [InlineData(".gitignore")]
    [InlineData(".ignore")]
    [InlineData(".fdignore")]
    public async Task EachIgnoreFileExcludesMatchingSkillDirectories(string file)
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, file, "BLOCKED/\n");
            await WriteAsync(root, "blocked/SKILL.md", Skill("blocked"));
            await WriteAsync(root, "keep/SKILL.md", Skill("keep"));

            var resources = await LoadAsync(root);

            Assert.Equal("keep", Assert.Single(resources.Skills).Name);
        });
    }

    [Fact]
    public async Task PunctuationClassDoesNotMatchTheDirectorySeparator()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, ".ignore", "*[[:punct:]]*.md\n");
            await WriteAsync(root, "child/SKILL.md", Skill("child"));
            await WriteAsync(root, "literal$+.md", Skill("punctuation"));

            var resources = await LoadAsync(root);

            Assert.Equal("child", Assert.Single(resources.Skills).Name);
        });
    }

    [Fact]
    public async Task IgnoredDeclarationAllowsRootAliasesAndReincludedChildDeclaration()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, ".ignore", "SKILL.md\n!child/SKILL.md\n");
            await WriteAsync(root, "SKILL.md", Skill("root"));
            await WriteAsync(root, "alias.md", Skill("alias"));
            await WriteAsync(root, "child/SKILL.md", Skill("child"));

            var resources = await LoadAsync(root);

            Assert.Equal(["alias", "child"], resources.Skills.Select(s => s.Name));
        });
    }

    [Fact]
    public async Task NestedBasenameRuleUsesPisRootRelativePrefix()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, "blocked/.ignore", "SKILL.md\n");
            await WriteAsync(root, "blocked/SKILL.md", Skill("blocked"));
            await WriteAsync(root, "blocked/deep/SKILL.md", Skill("deep"));

            var resources = await LoadAsync(root);

            Assert.Equal("deep", Assert.Single(resources.Skills).Name);
        });
    }

    [Fact]
    public async Task NestedGitignoreCanReincludeAfterRootFdignore()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, ".fdignore", "*/SKILL.md\n");
            await WriteAsync(root, "keep/.gitignore", "!SKILL.md\n");
            await WriteAsync(root, "blocked/SKILL.md", Skill("blocked"));
            await WriteAsync(root, "keep/SKILL.md", Skill("keep"));

            var resources = await LoadAsync(root);

            Assert.Equal("keep", Assert.Single(resources.Skills).Name);
        });
    }

    [Fact]
    public async Task ChildNegationDoesNotRestoreAnIgnoredParentDirectory()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, ".ignore", "blocked/\n!blocked/SKILL.md\n");
            await WriteAsync(root, "blocked/SKILL.md", Skill("blocked"));
            await WriteAsync(root, "keep/SKILL.md", Skill("keep"));

            var resources = await LoadAsync(root);

            Assert.Equal("keep", Assert.Single(resources.Skills).Name);
        });
    }

    [Fact]
    public async Task EscapedBangRetainsPisPrefixConversionBehavior()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, ".ignore", "\\!guide.md\n");
            await WriteAsync(root, "!guide.md", Skill("bang"));

            var resources = await LoadAsync(root);

            Assert.Equal("bang", Assert.Single(resources.Skills).Name);
        });
    }

    [Fact]
    public async Task PromptDiscoveryDoesNotApplySkillIgnoreFiles()
    {
        await WithRootAsync(async root =>
        {
            await WriteAsync(root, ".gitignore", "*.md\n");
            await WriteAsync(root, ".hidden.md", "hidden prompt");
            await WriteAsync(root, "visible.md", "visible prompt");

            var resources = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                discoverPrompts: false, additionalPrompts: [root]);

            Assert.Equal([".hidden", "visible"], resources.Prompts.Select(p => p.Name));
        });
    }

    private static Task<ResourceCatalog> LoadAsync(string root) => ResourceCatalog.LoadAsync(root, root, false,
        discoverSkills: false, discoverPrompts: false, additionalSkills: [root]);

    private static string Skill(string name) => $"---\nname: {name}\ndescription: {name} guide\n---\n{name} body";

    private static async Task WriteAsync(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
    }

    private static async Task WithRootAsync(Func<string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
