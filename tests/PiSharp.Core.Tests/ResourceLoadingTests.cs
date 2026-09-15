using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class ResourceLoadingTests
{
    [Fact]
    public void DiscoversSkillsWithUserPrecedenceAndMetadataFormatting()
    {
        using var workspace = new TemporaryDirectory();
        var home = Path.Combine(workspace.Path, "home");
        Directory.CreateDirectory(Path.Combine(home, ".pi", "agent", "skills", "shared"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".pi", "skills", "shared"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".pi", "skills", "local"));

        File.WriteAllText(
            Path.Combine(home, ".pi", "agent", "skills", "shared", "SKILL.md"),
            "---\nname: shared\ndescription: user skill\n---\nUser instructions");
        File.WriteAllText(
            Path.Combine(workspace.Path, ".pi", "skills", "shared", "SKILL.md"),
            "---\nname: shared\ndescription: project skill\n---\nProject instructions");
        File.WriteAllText(
            Path.Combine(workspace.Path, ".pi", "skills", "local", "SKILL.md"),
            "---\nname: local\ndescription: local skill\ndisable-model-invocation: true\n---\nLocal instructions");

        var result = new SkillCatalog().Discover(workspace.Path, home);

        Assert.Equal(["shared", "local"], result.Skills.Select(skill => skill.Name));
        Assert.Equal("user skill", result.Skills[0].Description);
        Assert.Contains("<name>shared</name>", SkillCatalog.FormatForPrompt(result.Skills));
        Assert.DoesNotContain("<name>local</name>", SkillCatalog.FormatForPrompt(result.Skills));
    }

    [Fact]
    public void ReportsInvalidSkillMetadataAndStillLoadsSkill()
    {
        using var workspace = new TemporaryDirectory();
        var skillDirectory = Path.Combine(workspace.Path, ".pi", "skills", "bad--name");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: Bad_Name\ndescription: usable\n---\nBody");

        var result = new SkillCatalog().Discover(workspace.Path, workspace.Path);

        Assert.Single(result.Skills);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("invalid", StringComparison.Ordinal));
    }

    [Fact]
    public void ExpandsExplicitSkillCommandAndStripsFrontmatter()
    {
        using var workspace = new TemporaryDirectory();
        var skillDirectory = Path.Combine(workspace.Path, ".pi", "skills", "review");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            "---\nname: review\ndescription: review code\n---\nCheck the diff.");
        var skill = new SkillCatalog().Discover(workspace.Path, workspace.Path).Skills;

        var expanded = SkillCatalog.ExpandCommand("/skill:review the patch", skill);

        Assert.Contains("Check the diff.", expanded);
        Assert.Contains("the patch", expanded);
        Assert.DoesNotContain("description:", expanded);
    }

    [Fact]
    public void ExpandsPromptTemplateArgumentsLikePi()
    {
        var template = new PromptTemplate("review", "Review", "$1|$@|${2:-default}|${@:2}|${@:2:1}", "review.md", null);

        var expanded = PromptTemplateCatalog.Expand("/review file \"long name\"", [template]);

        Assert.Equal("file|file long name|long name|long name|long name", expanded);
    }

    [Fact]
    public void LoadsPromptTemplatesFromFrontmatter()
    {
        using var workspace = new TemporaryDirectory();
        var directory = Path.Combine(workspace.Path, ".pi", "prompts");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "review.md"),
            "---\ndescription: Review changes\nargument-hint: [path]\n---\nReview $1");

        var templates = new PromptTemplateCatalog().Discover(workspace.Path, workspace.Path);

        var template = Assert.Single(templates);
        Assert.Equal("Review changes", template.Description);
        Assert.Equal("[path]", template.ArgumentHint);
        Assert.Equal("Review file", PromptTemplateCatalog.Expand("/review file", templates));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for files held by a test runner.
            }
        }
    }
}
