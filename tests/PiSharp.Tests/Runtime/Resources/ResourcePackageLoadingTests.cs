using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourcePackageLoadingTests
{
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
            Assert.DoesNotContain(catalog.Diagnostics, diagnostic => diagnostic.Path.StartsWith(package, StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
