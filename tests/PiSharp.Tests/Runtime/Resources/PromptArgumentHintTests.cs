using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class PromptArgumentHintTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepositoryDuplicatePromptUsesNormalTrustedProjectDiscovery(bool trusted)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "PiSharp.slnx")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var root = Path.Combine(Path.GetTempPath(), "pisharp-dup-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "prompts"));
        Directory.CreateDirectory(Path.Combine(root, "agent"));
        try
        {
            File.Copy(Path.Combine(repository.FullName, ".pi", "prompts", "dup.md"),
                Path.Combine(root, ".pi", "prompts", "dup.md"));
            var catalog = await ResourceCatalog.LoadAsync(root, Path.Combine(root, "agent"), trusted,
                discoverSkills: false);
            if (!trusted)
            {
                Assert.Empty(catalog.Prompts);
                Assert.Equal("/dup https://example.test/issues/1", await catalog.ResolveInputAsync("/dup https://example.test/issues/1"));
                return;
            }
            var prompt = Assert.Single(catalog.Prompts);
            Assert.Equal("dup", prompt.Name);
            Assert.Equal("<issue-url>", prompt.ArgumentHint);
            Assert.Equal("Find potential duplicates of a GitHub issue", prompt.Description);
            Assert.Equal("project", prompt.SourceInfo.Scope);
            Assert.Equal(prompt.Template.Replace("$1", "https://example.test/issues/1", StringComparison.Ordinal),
                await catalog.ResolveInputAsync("/dup https://example.test/issues/1"));
            Assert.EndsWith("Do not post, label, or close anything.", prompt.Template);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("argument-hint: <subject>", "<subject>")]
    [InlineData("argument-hint: \"\"", null)]
    [InlineData("argument-hint: \" \"", " ")]
    [InlineData("argument-hint: 123", null)]
    [InlineData("argument-hint: true", null)]
    [InlineData("argument-hint: null", null)]
    [InlineData("argument-hint: {name: arg}", null)]
    [InlineData("argument-hint: [arg]", null)]
    [InlineData("argument-hint: \"true\"", "true")]
    [InlineData("argument-hint: !!str true", "true")]
    [InlineData("value: &hint Guide\nargument-hint: *hint", "Guide")]
    [InlineData("argument-hint: |\n  <subject>\n  [detail]", "<subject>\n[detail]\n")]
    [InlineData("argument-hint: >-\n  <subject>\n  [detail]", "<subject> [detail]")]
    [InlineData("Argument-Hint: arg", null)]
    [InlineData("argument-hint: \"123\"", "123")]
    [InlineData("argument-hint: \"🌍 \uFEFF\"", "🌍 \uFEFF")]
    public async Task PreservesOnlyNonemptyTypedStringsWithoutChangingTemplateExpansion(string yaml, string? expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-prompt-hint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "guide.md");
            await File.WriteAllTextAsync(path, $"---\ndescription: Guide\n{yaml}\n---\nReview $1");
            var catalog = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                discoverPrompts: false, additionalPrompts: [path]);

            Assert.Equal(expected, Assert.Single(catalog.Prompts).ArgumentHint);
            Assert.Empty(catalog.Diagnostics);
            Assert.Equal("Review subject", await catalog.ResolveInputAsync("/guide subject"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
