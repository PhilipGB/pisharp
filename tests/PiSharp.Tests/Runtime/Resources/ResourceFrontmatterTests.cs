using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceFrontmatterTests
{
    [Theory]
    [InlineData(true, "comment")]
    [InlineData(false, "comment")]
    [InlineData(true, "escape")]
    [InlineData(false, "escape")]
    [InlineData(true, "flow")]
    [InlineData(false, "flow")]
    [InlineData(true, "anchor")]
    [InlineData(false, "anchor")]
    [InlineData(true, "number")]
    [InlineData(false, "number")]
    [InlineData(true, "boolean")]
    [InlineData(false, "boolean")]
    [InlineData(true, "case")]
    [InlineData(false, "case")]
    [InlineData(true, "duplicate")]
    [InlineData(false, "duplicate")]
    public async Task MetadataUsesYamlSyntaxTypesAndCaseSensitiveKeys(bool skill, string kind)
    {
        var root = NewRoot();
        var path = Path.Combine(root, skill ? "SKILL.md" : "guide.md");
        var yaml = kind switch
        {
            "comment" => "name: guide\ndescription: Guide # comment",
            "escape" => "name: guide\ndescription: \"First\\nsecond\"",
            "flow" => "{name: guide, description: Guide}",
            "anchor" => "name: guide\nvalue: &shared Guide\ndescription: *shared",
            "number" => "name: guide\ndescription: 123",
            "boolean" => "name: guide\ndescription: true",
            "case" => "name: guide\nDescription: Guide",
            _ => "name: guide\ndescription: Guide\ndescription: Later"
        };
        try
        {
            await File.WriteAllTextAsync(path, $"---\n{yaml}\n---\nbody");
            var catalog = await LoadAsync(root, skill, [path]);
            if (kind == "duplicate")
            {
                Assert.Empty(catalog.Skills);
                Assert.Empty(catalog.Prompts);
                Assert.Equal(path, Assert.Single(catalog.Diagnostics).Path);
            }
            else if (skill && kind is "number" or "boolean" or "case")
            {
                Assert.Empty(catalog.Skills);
                Assert.Equal("description is required", Assert.Single(catalog.Diagnostics).Message);
            }
            else
            {
                var expected = kind == "escape" ? "First\nsecond" : kind is "number" or "boolean" or "case" ? "body" : "Guide";
                Assert.Equal(expected, skill ? Assert.Single(catalog.Skills).Description : Assert.Single(catalog.Prompts).Description);
                Assert.Empty(catalog.Diagnostics);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("\"true\"", false)]
    public async Task InvocationPolicyRequiresAnActualYamlBoolean(string input, bool expected)
    {
        var root = NewRoot();
        var path = Path.Combine(root, "SKILL.md");
        try
        {
            await File.WriteAllTextAsync(path, $"---\nname: guide\ndescription: Guide\ndisable-model-invocation: {input}\n---\nbody");
            Assert.Equal(expected, Assert.Single((await LoadAsync(root, true, [path])).Skills).ExplicitOnly);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NonStringSkillNameFallsBackToDirectory()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "SKILL.md");
        try
        {
            await File.WriteAllTextAsync(path, "---\nname: true\ndescription: Guide\n---\nbody");
            Assert.Equal(Path.GetFileName(root), Assert.Single((await LoadAsync(root, true, [path])).Skills).Name);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidYamlWarnsAndLaterResourceLoads(bool skill)
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "first"));
        Directory.CreateDirectory(Path.Combine(root, "second"));
        var first = Path.Combine(root, "first", skill ? "SKILL.md" : "first.md");
        var second = Path.Combine(root, "second", skill ? "SKILL.md" : "second.md");
        try
        {
            await File.WriteAllTextAsync(first, "---\nname: first\ndescription: First\nignored: [broken\n---\nbody");
            await File.WriteAllTextAsync(second, "---\nname: second\ndescription: Second\n---\nbody");
            var catalog = await LoadAsync(root, skill, [first, second]);
            var warning = Assert.Single(catalog.Diagnostics);
            Assert.Equal("warning", warning.Type);
            Assert.Equal(first, warning.Path);
            Assert.Equal(first, warning.SourceInfo!.Path);
            Assert.Equal(second, skill ? Assert.Single(catalog.Skills).Path : Assert.Single(catalog.Prompts).Path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NonDeclaredSkillWithoutDescriptionIsIgnoredSilently()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "alias.md");
        try
        {
            await File.WriteAllTextAsync(path, "---\nname: alias\n---\nbody");
            var catalog = await LoadAsync(root, true, [path]);
            Assert.Empty(catalog.Skills);
            Assert.Empty(catalog.Diagnostics);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NelDescriptionRemainsAvailableLikeJavascriptText()
    {
        var root = NewRoot();
        var path = Path.Combine(root, "SKILL.md");
        try
        {
            await File.WriteAllTextAsync(path, "---\nname: guide\ndescription: \"\\u0085\"\n---\nbody");
            Assert.Equal("\u0085", Assert.Single((await LoadAsync(root, true, [path])).Skills).Description);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("\uFEFF\uFEFF\nbody", "body")]
    [InlineData("\u0085\nbody", "\u0085")]
    public async Task PromptDescriptionUsesJavascriptWhitespaceRules(string text, string expected)
    {
        var root = NewRoot();
        var path = Path.Combine(root, "guide.md");
        try
        {
            await File.WriteAllTextAsync(path, text);
            Assert.Equal(expected, Assert.Single((await LoadAsync(root, false, [path])).Prompts).Description);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("name: guide\ndescription: Guide\ndescription: Later", "Map keys must be unique at line 3, column 1:\n\ndescription: Guide\ndescription: Later\n^\n")]
    [InlineData("name: guide\ndescription: Guide\nunused: [invalid", "Flow sequence in block collection must be sufficiently indented and end with a ] at line 3, column 17:\n\nunused: [invalid\n                ^\n")]
    [InlineData("name: guide\ndescription: Guide\nunused:\n  one: a\n  one: b", "Map keys must be unique at line 5, column 3:\n\n  one: a\n  one: b\n  ^\n")]
    public async Task CommonSyntaxWarningsRetainPiLocationAndSourceContext(string yaml, string expected)
    {
        var root = NewRoot();
        var path = Path.Combine(root, "guide.md");
        try
        {
            await File.WriteAllTextAsync(path, $"---\n{yaml}\n---\nbody");
            Assert.Equal(expected, Assert.Single((await LoadAsync(root, false, [path])).Diagnostics).Message);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-resource-yaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Task<ResourceCatalog> LoadAsync(string root, bool skill, IReadOnlyList<string> paths) =>
        ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false,
            additionalSkills: skill ? paths : [], additionalPrompts: skill ? [] : paths);
}
