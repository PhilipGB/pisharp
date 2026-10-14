using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class LocalResourcePathRulesTests
{
    [Theory]
    [InlineData("SKILL.md", "!skills/guide", false)]
    [InlineData("skill.md", "!skills/guide", true)]
    [InlineData("SKILL.md", "!*.md|+skills/guide", true)]
    [InlineData("skill.md", "!*.md|+skills/guide", false)]
    [InlineData("SKILL.md", "+skills/guide|-skills/guide", false)]
    [InlineData("skill.md", "+skills/guide|-skills/guide", true)]
    public void ParentOverridesApplyOnlyToTheExactSkillDeclarationFilename(string file, string rules, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-skill-declaration-case");
        var path = Path.Combine(root, "skills", "guide", file);

        Assert.Equal(expected, LocalResourcePathRules.IsEnabledByOverrides(path, rules.Split('|'), root, skillPath: true));
    }

    [Theory]
    [InlineData("extensions/plugin.dll", "extensions/*.dll", false)]
    [InlineData("prompts/review.md", "prompts/*.md", false)]
    [InlineData("themes/custom.json", "themes/*.json", false)]
    [InlineData("skills/pdf/SKILL.md", "skills/pdf*", true)]
    public void GlobAndExactOverridesExpandHomeForEachResource(string relativePath, string glob, bool skillPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, "pisharp-resource-home-fixture");
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var exact = skillPath ? "skills/pdf" : relativePath;
        var exclusion = "!~/pisharp-resource-home-fixture/" + glob;
        var include = "+~/pisharp-resource-home-fixture/" + exact;
        var exclude = "-~/pisharp-resource-home-fixture/" + exact;
        var baseDirectory = Path.Combine(Path.GetTempPath(), "pisharp-resource-home-base");

        Assert.False(LocalResourcePathRules.IsEnabledByOverrides(path, [exclusion], baseDirectory, skillPath));
        Assert.True(LocalResourcePathRules.IsEnabledByOverrides(path, [exclusion, include], baseDirectory, skillPath));
        Assert.False(LocalResourcePathRules.IsEnabledByOverrides(path, [include, exclude], baseDirectory, skillPath));
    }

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
