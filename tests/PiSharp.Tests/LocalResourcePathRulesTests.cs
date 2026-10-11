using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class LocalResourcePathRulesTests
{
    [Theory]
    [InlineData("docs/.hidden.md", "!docs/**", true)]
    [InlineData("docs/.hidden.md", "!docs/*.md", true)]
    [InlineData("docs/.hidden.md", "!docs/.*", false)]
    [InlineData("docs/.hidden.md", "!docs/[.]hidden.md", false)]
    [InlineData("docs/.hidden/file.md", "!docs/**", true)]
    [InlineData("docs/.hidden/file.md", "!**/.hidden/file.md", false)]
    [InlineData("docs/readme.md", "!docs/*.md", false)]
    [InlineData("docs/review.md", "!docs/*.{md,json}", false)]
    [InlineData("docs/review.json", "!docs/*.{md,json}", false)]
    [InlineData("docs/review.txt", "!docs/*.{md,json}", true)]
    [InlineData("tasks/issue-2.md", "!tasks/issue-{1..3}.md", false)]
    public void GlobOverridesFollowPiMinimatchRules(string relativePath, string exclude, bool expectedEnabled)
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "pisharp-resource-glob");
        var path = Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expectedEnabled,
            LocalResourcePathRules.IsEnabledByOverrides(path, [exclude], baseDirectory));
    }
}
