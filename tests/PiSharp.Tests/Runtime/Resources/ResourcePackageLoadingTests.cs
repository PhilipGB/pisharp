using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourcePackageLoadingTests
{
    [Theory]
    [InlineData("file://remote/tmp/package", "File URL host must be \"localhost\" or empty on linux")]
    [InlineData("file:///tmp/package%2fchild", "File URL path must not include encoded / characters")]
    [InlineData("file:///tmp/bad%", "URI malformed")]
    [InlineData("file:///tmp/%E0%A4", "URI malformed")]
    public async Task InvalidFileUrlPackageSourcesReportSourcePathErrors(string source, string message)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-package-url-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => ResourceCatalog.LoadAsync(root, root,
                false, userPackages: [new(source)]));
            Assert.Equal(message, error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("", "", "package")]
    [InlineData("localhost", "", "package")]
    [InlineData("", "?ignored#fragment", "package space")]
    [InlineData("", "?bad%#%E0%A4", "package π%")]
    public async Task FileUrlPackageSourcesLoadDecodedLocalPaths(string host, string suffix, string directory)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-package-url-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var package = Path.Combine(root, directory);
        var path = Path.Combine(package, "prompts", "review.md");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, "Review URL package");
            var source = new UriBuilder(new Uri(package + Path.DirectorySeparatorChar)) { Host = host }.Uri.AbsoluteUri + suffix;

            var catalog = await ResourceCatalog.LoadAsync(root, agent, false,
                discoverSkills: false, userPackages: [new(source)]);

            var prompt = Assert.Single(catalog.Prompts);
            Assert.Equal(path, prompt.Path);
            Assert.Equal(new ResourceSourceInfo(path, source, "user", "package", package), prompt.SourceInfo);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../package")]
    [InlineData("../package/")]
    [InlineData(" \t../package\r\n")]
    [InlineData("\uFEFF\u00A0../package\u2029\uFEFF")]
    [InlineData(" \t\r\n")]
    public async Task PackageSourceTrimsJavaScriptWhitespaceAndRetainsDeclaredProvenance(string source)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-package-source-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var package = string.IsNullOrWhiteSpace(source) ? agent : Path.Combine(root, "package");
        var path = Path.Combine(package, "prompts", "review.md");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, "Review package");

            var catalog = await ResourceCatalog.LoadAsync(root, agent, false,
                discoverSkills: false, userPackages: [new(source)]);
            var prompt = Assert.Single(catalog.Prompts);
            Assert.Equal(path, prompt.Path);
            Assert.Equal(new ResourceSourceInfo(path, source, "user", "package", package), prompt.SourceInfo);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(".hidden/*.md", false)]
    [InlineData(".hidden/secret.md", true)]
    public async Task ManifestGlobsHideDotPathsWhileExactEntriesCanSelectThem(string entry, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-package-hidden-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var package = Path.Combine(root, "package");
        var path = Path.Combine(package, ".hidden", "secret.md");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, "Hidden prompt");
            await File.WriteAllTextAsync(Path.Combine(package, "package.json"),
                System.Text.Json.JsonSerializer.Serialize(new { pi = new { prompts = new[] { entry } } }));

            var catalog = await ResourceCatalog.LoadAsync(root, agent, false, discoverSkills: false,
                userPackages: [new("../package")]);

            Assert.Equal(expected, catalog.Prompts.Any(prompt => prompt.Path == path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisabledProjectPackageEntryWinsCanonicalIdentityBeforeEnabledPersonalAlias()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-package-canonical-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "project");
        var package = Path.Combine(root, "package");
        var path = Path.Combine(package, "skills", "guide", "SKILL.md");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, "---\nname: canonical-package-guide\ndescription: Guide\n---\nbody");
            Directory.CreateSymbolicLink(Path.Combine(root, "alias-package"), package);

            var catalog = await ResourceCatalog.LoadAsync(project, agent, true,
                userPackages: [new("../alias-package")],
                projectPackages: [new("../../package", Skills: [], HasFilter: true)]);

            Assert.DoesNotContain(catalog.Skills, skill => skill.Name == "canonical-package-guide");
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersonalPackageLoadsDeclaredSkillAndPromptWithPackageProvenance(bool manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-package-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        var package = Path.Combine(root, "package");
        var skill = Path.Combine(package, "skills/package-guide/SKILL.md");
        var prompt = Path.Combine(package, "prompts/package-review.md");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        Directory.CreateDirectory(Path.GetDirectoryName(prompt)!);
        try
        {
            await File.WriteAllTextAsync(skill, "---\nname: package-guide\ndescription: Package guide\n---\nPackage body");
            await File.WriteAllTextAsync(prompt, "---\ndescription: Package review\n---\nReview $1");
            if (manifest)
                await File.WriteAllTextAsync(Path.Combine(package, "package.json"),
                    "{\"pi\":{\"skills\":[\"skills\"],\"prompts\":[\"prompts/package-review.md\"]}}");

            var catalog = await ResourceCatalog.LoadAsync(root, agent, false,
                userPackages: [new("../package")]);

            var loadedSkill = Assert.Single(catalog.Skills, resource => resource.Name == "package-guide");
            var loadedPrompt = Assert.Single(catalog.Prompts, resource => resource.Name == "package-review");
            Assert.NotNull(loadedSkill);
            Assert.NotNull(loadedPrompt);
            Assert.Equal("Package guide", loadedSkill.Description);
            Assert.Equal("Review $1", loadedPrompt.Template);
            Assert.Equal(new ResourceSourceInfo(skill, "../package", "user", "package", package), loadedSkill.SourceInfo);
            Assert.Equal(new ResourceSourceInfo(prompt, "../package", "user", "package", package), loadedPrompt.SourceInfo);
            Assert.DoesNotContain(catalog.Diagnostics, diagnostic => diagnostic.Path?.StartsWith(package, StringComparison.Ordinal) == true);
        }
        finally { Directory.Delete(root, true); }
    }
}
