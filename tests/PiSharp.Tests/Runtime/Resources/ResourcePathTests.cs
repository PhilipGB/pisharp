using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourcePathTests
{
    [Theory]
    [InlineData("SKILL.md")]
    [InlineData("alias.md")]
    public async Task ExplicitMarkdownSkillAliasesLoad(string name)
    {
        var root = NewRoot();
        var path = Path.Combine(root, name);
        try
        {
            await File.WriteAllTextAsync(path, "---\nname: guide\ndescription: Guide\n---\nbody");
            var catalog = await LoadAsync(root, true, path);
            Assert.Equal(path, Assert.Single(catalog.Skills).Path);
            Assert.Empty(catalog.Diagnostics);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true, "missing")]
    [InlineData(false, "missing")]
    [InlineData(true, "wrong-extension")]
    [InlineData(false, "wrong-extension")]
    [InlineData(true, "uppercase")]
    [InlineData(false, "uppercase")]
    [InlineData(true, "broken-link")]
    [InlineData(false, "broken-link")]
    public async Task InvalidExplicitPathsFollowResourceSpecificDiagnostics(bool skill, string kind)
    {
        if (kind == "broken-link" && OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        var path = Path.Combine(root, kind == "uppercase" ? "GUIDE.MD" : kind == "wrong-extension" ? "guide.txt" : "guide.md");
        try
        {
            if (kind == "broken-link") File.CreateSymbolicLink(path, Path.Combine(root, "absent-target"));
            else if (kind != "missing") await File.WriteAllTextAsync(path, "---\nname: guide\ndescription: Guide\n---\nbody");
            var catalog = await LoadAsync(root, skill, path);
            Assert.Empty(catalog.Skills);
            Assert.Empty(catalog.Prompts);
            if (skill)
            {
                var warning = Assert.Single(catalog.Diagnostics);
                Assert.Equal("warning", warning.Type);
                Assert.Equal(path, warning.Path);
                Assert.Equal(kind is "missing" or "broken-link" ? "skill path does not exist" : "skill path is not a markdown file", warning.Message);
            }
            else Assert.Empty(catalog.Diagnostics);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("space name.md")]
    [InlineData("multi.dot.md")]
    public async Task PromptNamesRetainFilenameCharacters(string name)
    {
        var root = NewRoot();
        var path = Path.Combine(root, name);
        try
        {
            await File.WriteAllTextAsync(path, "body");
            var catalog = await LoadAsync(root, false, path);
            Assert.Equal(Path.GetFileNameWithoutExtension(name), Assert.Single(catalog.Prompts).Name);
            Assert.Equal("body", catalog.ExpandPrompt(Path.GetFileNameWithoutExtension(name), ""));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static Task<ResourceCatalog> LoadAsync(string root, bool skill, string path) =>
        ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false,
            additionalSkills: skill ? [path] : [], additionalPrompts: skill ? [] : [path]);
}
