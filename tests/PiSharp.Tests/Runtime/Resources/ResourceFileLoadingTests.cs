using System.Text;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceFileLoadingTests
{
    [Theory]
    [InlineData(true, "large")]
    [InlineData(false, "large")]
    [InlineData(true, "utf8")]
    [InlineData(false, "utf8")]
    [InlineData(true, "cr")]
    [InlineData(false, "cr")]
    public async Task CompleteResourceTextUsesReplacementUtf8AndNormalizedNewlines(bool skill, string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, skill ? "SKILL.md" : "guide.md");
        var header = "---\nname: guide\ndescription: Guide\n---\n";
        var body = kind == "large" ? new string('x', 66560) : kind == "utf8" ? "bad \uFFFD tail" : "body\nsecond";
        var bytes = kind == "utf8" ? Encoding.UTF8.GetBytes(header + "bad ").Concat(new byte[] { 0xff })
            .Concat(Encoding.UTF8.GetBytes(" tail")).ToArray()
            : Encoding.UTF8.GetBytes((header + body).Replace("\n", kind == "cr" ? "\r" : "\n", StringComparison.Ordinal));
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            var catalog = await LoadAsync(root, skill, [path]);
            Assert.Empty(catalog.Diagnostics);
            if (skill)
            {
                Assert.Equal("Guide", Assert.Single(catalog.Skills).Description);
                var text = await catalog.InvokeSkillAsync("guide", "");
                Assert.Equal($"<skill name=\"guide\" location=\"{path}\">\nReferences are relative to {root}.\n\n{body}\n</skill>", text);
            }
            else Assert.Equal(body, Assert.Single(catalog.Prompts).Template);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExactlyOneBomIsRemovedBeforeDetectingFrontmatter(bool skill)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-bom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, skill ? "SKILL.md" : "guide.md");
        const string contents = "\uFEFF---\nname: guide\ndescription: Guide\n---\nbody";
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("\uFEFF" + contents));
            var catalog = await LoadAsync(root, skill, [path]);
            if (skill)
            {
                Assert.Empty(catalog.Skills);
                Assert.Equal("description is required", Assert.Single(catalog.Diagnostics).Message);
            }
            else Assert.Equal(contents, Assert.Single(catalog.Prompts).Template);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreadableFileWarnsAndLaterResourcesStillLoad(bool skill)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-denied-" + Guid.NewGuid().ToString("N"));
        var denied = Path.Combine(root, "denied", skill ? "SKILL.md" : "denied.md");
        var valid = Path.Combine(root, "valid", skill ? "SKILL.md" : "valid.md");
        Directory.CreateDirectory(Path.GetDirectoryName(denied)!);
        Directory.CreateDirectory(Path.GetDirectoryName(valid)!);
        try
        {
            await File.WriteAllTextAsync(denied, "---\nname: denied\ndescription: Denied\n---\nfirst");
            await File.WriteAllTextAsync(valid, "---\nname: valid\ndescription: Valid\n---\nsecond");
            File.SetUnixFileMode(denied, UnixFileMode.None);
            var catalog = await LoadAsync(root, skill, [denied, valid]);
            var warning = Assert.Single(catalog.Diagnostics);
            Assert.Equal("warning", warning.Type);
            Assert.Equal(denied, warning.Path);
            Assert.Equal(denied, warning.SourceInfo!.Path);
            Assert.Equal(valid, skill ? Assert.Single(catalog.Skills).Path : Assert.Single(catalog.Prompts).Path);
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<ResourceCatalog> LoadAsync(string root, bool skill, IReadOnlyList<string> paths) =>
        ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false,
            additionalSkills: skill ? paths : [], additionalPrompts: skill ? [] : paths);
}
