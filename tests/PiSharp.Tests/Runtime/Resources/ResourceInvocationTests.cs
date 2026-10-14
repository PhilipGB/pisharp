using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ResourceInvocationTests
{
    [Theory]
    [InlineData("/skill:missing")]
    [InlineData("/skill:")]
    [InlineData("/skill:guide\targ")]
    [InlineData("/skill:guide\narg")]
    [InlineData("/skill:guide\rarg")]
    [InlineData("/skill:guide\u00a0arg")]
    public async Task UnknownOrNonSpaceDelimitedSkillInputPassesThrough(string input)
    {
        await WithCatalogAsync(async (catalog, _) => Assert.Equal(input, await catalog.ResolveInputAsync(input)));
    }

    [Fact]
    public async Task SkillArgumentsUseJavascriptWhitespaceTrimming()
    {
        await WithCatalogAsync(async (catalog, _) =>
        {
            var expanded = await catalog.ResolveInputAsync("/skill:guide \ufeffarg\ufeff");

            Assert.EndsWith("</skill>\n\narg", expanded);
        });
    }

    [Fact]
    public async Task UnknownSkillNameCanFallThroughToAPromptTemplate()
    {
        if (OperatingSystem.IsWindows()) return;
        await WithCatalogAsync(async (catalog, _) =>
            Assert.Equal("Template arg", await catalog.ResolveInputAsync("/skill:template arg")));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("invalid-yaml")]
    public async Task FailedSkillExpansionReportsTheErrorAndRetainsInput(string action)
    {
        await WithCatalogAsync(async (catalog, path) =>
        {
            if (action == "delete") File.Delete(path);
            else await File.WriteAllTextAsync(path, "---\ndescription: [broken\n---\nbody");
            var errors = new List<ResourceExpansionError>();

            var input = "/skill:guide args";
            var result = await catalog.ResolveInputAsync(input, reportError: error =>
            {
                errors.Add(error);
                return Task.CompletedTask;
            });

            Assert.Equal(input, result);
            var error = Assert.Single(errors);
            Assert.Equal(path, error.ExtensionPath);
            Assert.Equal("skill_expansion", error.Event);
            Assert.Contains(action == "delete" ? "ENOENT" : "Flow sequence", error.Error);
        });
    }

    [Fact]
    public async Task CancelledSkillReadPropagatesCancellationWithoutReportingAResourceError()
    {
        await WithCatalogAsync(async (catalog, _) =>
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var errors = new List<ResourceExpansionError>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                catalog.ResolveInputAsync("/skill:guide args", cancellation.Token, error =>
                {
                    errors.Add(error);
                    return Task.CompletedTask;
                }));

            Assert.Empty(errors);
        });
    }

    [Fact]
    public async Task UnmatchedTemplateQuoteIsAccepted()
    {
        await WithCatalogAsync(async (catalog, _) =>
            Assert.Equal("Review unfinished | unfinished", await catalog.ResolveInputAsync("/review \"unfinished")));
    }

    private static async Task WithCatalogAsync(Func<ResourceCatalog, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-invocation-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "guide", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        Directory.CreateDirectory(Path.Combine(root, "prompts"));
        try
        {
            await File.WriteAllTextAsync(file, "---\nname: guide\ndescription: Guide\n---\nbody");
            await File.WriteAllTextAsync(Path.Combine(root, "prompts", "review.md"), "Review $1 | $@");
            if (!OperatingSystem.IsWindows())
                await File.WriteAllTextAsync(Path.Combine(root, "prompts", "skill:template.md"), "Template $1");
            var catalog = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false,
                additionalSkills: [file], additionalPrompts: [Path.Combine(root, "prompts")]);
            await action(catalog, file);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
