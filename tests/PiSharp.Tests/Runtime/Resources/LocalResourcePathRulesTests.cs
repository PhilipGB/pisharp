using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class LocalResourcePathRulesTests
{
    [Theory]
    [InlineData("docs/.hidden.md", "!docs/**", true)]
    [InlineData("docs/.hidden.md", "!docs/*.md", true)]
    [InlineData("docs/.hidden.md", "!docs/.*", false)]
    [InlineData("docs/.hidden.md", "!docs/[.]hidden.md", false)]
    [InlineData("docs/.foo", "!docs/[a.]*", true)]
    [InlineData("docs/.foo", "!docs/[.a]*", true)]
    [InlineData("docs/.foo", "!docs/[[:punct:]]*", true)]
    [InlineData("docs/.hidden/file.md", "!docs/**", true)]
    [InlineData("docs/.hidden/file.md", "!**/.hidden/file.md", false)]
    [InlineData("docs/readme.md", "!docs/*.md", false)]
    [InlineData("docs/review.md", "!docs/*.{md,json}", false)]
    [InlineData("docs/review.json", "!docs/*.{md,json}", false)]
    [InlineData("docs/review.txt", "!docs/*.{md,json}", true)]
    [InlineData("tasks/issue-2.md", "!tasks/issue-{1..3}.md", false)]
    [InlineData("tasks/issue-3.md", "!tasks/issue-{5..1..-2}.md", false)]
    [InlineData("tasks/issue-4.md", "!tasks/issue-{1..5..0}.md", false)]
    [InlineData("tasks/issue-4.md", "!tasks/issue-{1..5..2}.md", true)]
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

    [Theory]
    [InlineData("docs/a*.md", "!docs/a\\*.md", false)]
    [InlineData("docs/other.md", "!docs/a\\*.md", true)]
    [InlineData("docs/file?.md", "!docs/file\\?.md", false)]
    [InlineData("docs/[draft].md", "!docs/\\[draft\\].md", false)]
    [InlineData("docs/.private.md", "!docs/\\.private.md", false)]
    [InlineData("docs/file{1,2}.md", "!docs/file\\{1,2\\}.md", false)]
    [InlineData("docs/@(one|two).md", "!docs/\\@(one|two).md", false)]
    [InlineData("docs/a\\name.md", "!docs/a\\\\name.md", false)]
    [InlineData("docs/trailing\\", "!docs/trailing\\", false)]
    [InlineData("docs/-.md", "!docs/[a\\-c].md", false)]
    [InlineData("docs/].md", "!docs/[\\]].md", false)]
    public void PosixGlobOverridesTreatBackslashesAsMinimatchEscapes(
        string relativePath, string exclude, bool expectedEnabled)
    {
        if (OperatingSystem.IsWindows()) return;

        var baseDirectory = Path.Combine(Path.GetTempPath(), "pisharp-resource-posix-escape");
        var path = Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expectedEnabled,
            LocalResourcePathRules.IsEnabledByOverrides(path, [exclude], baseDirectory));
    }

    [Theory]
    [InlineData("docs/secret.md", "!!**/secret.md", true)]
    [InlineData("docs/other.md", "!!**/secret.md", false)]
    [InlineData("#docs/file.md", "!#docs/*", true)]
    [InlineData("#docs/file.md", "!\\#docs/*", false)]
    [InlineData("docs/9item", "!docs/[[:alnum:]]*", false)]
    [InlineData("docs/item", "!docs/[[:alpha:]]*", false)]
    [InlineData("docs/a", "!docs/[[:ascii:]]", false)]
    [InlineData("docs/ñ", "!docs/[[:ascii:]]", true)]
    [InlineData("docs/ ", "!docs/[[:blank:]]", false)]
    [InlineData("docs/\u0001", "!docs/[[:cntrl:]]", false)]
    [InlineData("docs/abc.md", "!docs/[[:alpha:]]*.md", false)]
    [InlineData("docs/123.md", "!docs/[[:digit:]]*.md", false)]
    [InlineData("docs/z", "!docs/[[:graph:]]", false)]
    [InlineData("docs/ ", "!docs/[[:graph:]]", true)]
    [InlineData("docs/a", "!docs/[[:lower:]]", false)]
    [InlineData("docs/\u0001", "!docs/[[:print:]]", false)]
    [InlineData("docs/A", "!docs/[[:print:]]", true)]
    [InlineData("docs/!", "!docs/[[:punct:]]", false)]
    [InlineData("docs/\t", "!docs/[[:space:]]", false)]
    [InlineData("docs/A", "!docs/[[:upper:]]", false)]
    [InlineData("docs/_", "!docs/[[:word:]]", false)]
    [InlineData("docs/F", "!docs/[[:xdigit:]]", false)]
    [InlineData("docs/g", "!docs/[[:xdigit:]]", true)]
    [InlineData("docs/a.md", "!docs/[^b].md", false)]
    [InlineData("docs/b.md", "!docs/[^b].md", true)]
    public void GlobOverridesFollowPiMinimatchNegationCommentsAndCharacterClasses(
        string relativePath, string exclude, bool expectedEnabled)
    {
        if (OperatingSystem.IsWindows()) return;

        var baseDirectory = Path.Combine(Path.GetTempPath(), "pisharp-resource-minimatch-prefix");
        var path = Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expectedEnabled,
            LocalResourcePathRules.IsEnabledByOverrides(path, [exclude], baseDirectory));
    }
}
