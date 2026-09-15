using PiSharp.Cli;

namespace PiSharp.Core.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void ParsesRepeatableResourcePathsAndDisableFlags()
    {
        var options = CliOptions.Parse(
        [
            "--model", "test-model",
            "--endpoint", "http://localhost:8000/v1",
            "--extension", "extensions/one.dll",
            "-e", "extensions/two.dll",
            "--skill", "skills/review",
            "--prompt-template", "prompts/review.md",
            "--no-extensions",
            "--no-skills",
            "--no-prompt-templates",
        ]);

        Assert.Equal(["extensions/one.dll", "extensions/two.dll"], options.ExtensionPaths);
        Assert.Equal(["skills/review"], options.SkillPaths);
        Assert.Equal(["prompts/review.md"], options.PromptTemplatePaths);
        Assert.True(options.NoExtensions);
        Assert.True(options.NoSkills);
        Assert.True(options.NoPromptTemplates);
    }
}
