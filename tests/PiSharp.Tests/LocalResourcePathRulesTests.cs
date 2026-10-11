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

    [Theory]
    [InlineData("src/file.ts", "!src/@(file|test).ts", false)]
    [InlineData("src/test.ts", "!src/+(file|test).ts", false)]
    [InlineData("src/a.ts", "!src/?(a).ts", false)]
    [InlineData("src/ab.ts", "!src/?(a).ts", true)]
    [InlineData("src/aa.ts", "!src/*(a).ts", false)]
    [InlineData("src/.config.json", "!src/@(.config|readme).json", false)]
    [InlineData("src/foo.ts", "!src/@(f@(oo|oo)|bar).ts", false)]
    [InlineData("src/b.ts", "!src/@(a|[b|c]).ts", false)]
    [InlineData("src/foo.ts", "!src/!(bar).ts", false)]
    [InlineData("src/bar.ts", "!src/!(bar).ts", true)]
    [InlineData("src/foobar.ts", "!src/foo!(bar).ts", true)]
    [InlineData("src/foobaz.ts", "!src/foo!(bar).ts", false)]
    public void GlobOverridesFollowPiExtGlobRules(string relativePath, string exclude, bool expectedEnabled)
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "pisharp-resource-extglob");
        var path = Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expectedEnabled,
            LocalResourcePathRules.IsEnabledByOverrides(path, [exclude], baseDirectory));
    }
}
