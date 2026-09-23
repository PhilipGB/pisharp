using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceCatalogTests
{
    [Fact]
    public async Task ProjectResourcesRequireTrustAndSkillsLoadOnDemand()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        var userSkill = Path.Combine(agent, "skills", "guide");
        var projectSkill = Path.Combine(project, ".pi", "skills", "secret");
        Directory.CreateDirectory(userSkill);
        Directory.CreateDirectory(projectSkill);
        Directory.CreateDirectory(Path.Combine(agent, "prompts"));
        Directory.CreateDirectory(Path.Combine(project, ".pi", "prompts"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(userSkill, "SKILL.md"), "---\nname: unit-guide\ndescription: A testing guide\n---\nUse references/guide.md");
            await File.WriteAllTextAsync(Path.Combine(projectSkill, "SKILL.md"), "---\nname: project-secret\ndescription: A private guide\ndisable-model-invocation: true\n---\nRun a script");
            await File.WriteAllTextAsync(Path.Combine(agent, "prompts", "review.md"), "---\ndescription: Review changes\n---\nReview $1 and ${2:-security}: $@");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "prompts", "internal.md"), "Internal details");
            var untrusted = await ResourceCatalog.LoadAsync(project, agent, false);
            Assert.DoesNotContain(untrusted.Skills, skill => skill.Name == "project-secret");
            Assert.DoesNotContain(untrusted.Prompts, item => item.Name == "internal");
            Assert.Contains("unit-guide", untrusted.SystemInstructions());
            Assert.DoesNotContain("Use references", untrusted.SystemInstructions());
            Assert.Equal("Review API compatibility and security: API compatibility", untrusted.ExpandPrompt("review", "\"API compatibility\""));
            Assert.Contains("Use references/guide.md", await untrusted.InvokeSkillAsync("unit-guide", "focus"));
            Assert.Equal("Review API compatibility and security: API compatibility", await untrusted.ResolveInputAsync("/review \"API compatibility\""));
            Assert.Contains("Use references/guide.md", await untrusted.ResolveInputAsync("/skill:unit-guide focus"));
            Assert.Equal("ordinary text", await untrusted.ResolveInputAsync("ordinary text"));
            await Assert.ThrowsAsync<ArgumentException>(() => untrusted.ResolveInputAsync("/skill:project-secret"));
            var trusted = await ResourceCatalog.LoadAsync(project, agent, true);
            Assert.Contains(trusted.Skills, skill => skill.Name == "project-secret");
            Assert.DoesNotContain("project-secret", trusted.SystemInstructions());
            Assert.Contains("Run a script", await trusted.InvokeSkillAsync("project-secret", ""));
            Assert.Contains(trusted.Prompts, item => item.Name == "internal");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidResourcesDoNotAdvertiseAndBadArgumentsFail()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resources-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "skills", "bad");
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), "---\nname: Bad Name\ndescription: invalid\n---\ntext");
            var resources = await ResourceCatalog.LoadAsync(root, root, false);
            Assert.DoesNotContain(resources.Skills, skill => skill.Name == "Bad Name");
            Assert.Throws<ArgumentException>(() => resources.ExpandPrompt("absent", ""));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
