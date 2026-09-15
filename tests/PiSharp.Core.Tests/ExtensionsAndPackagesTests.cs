using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class ExtensionsAndPackagesTests
{
    [Fact]
    public void DiscoversPiPackageManifestAndResolvesResourcePaths()
    {
        using var workspace = new TemporaryDirectory();
        var packageRoot = Path.Combine(workspace.Path, ".pi", "packages", "sample");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            "{\"name\":\"sample-package\",\"pi\":{\"extensions\":[\"bin\"],\"skills\":[\"skills\",\"../outside\"],\"prompts\":[\"prompts/review.md\"]}}");

        var result = new PiPackageCatalog().Discover(workspace.Path, workspace.Path);

        var package = Assert.Single(result.Packages);
        Assert.Equal("sample-package", package.Name);
        Assert.Equal(Path.Combine(packageRoot, "skills"), Assert.Single(result.SkillPaths));
        Assert.Equal(Path.Combine(packageRoot, "prompts", "review.md"), Assert.Single(result.PromptPaths));
        Assert.Equal(Path.Combine(packageRoot, "bin"), Assert.Single(result.ExtensionPaths));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("escapes package root", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtensionHostTransformsInputPublishesEventsAndRunsCommands()
    {
        var host = new PiSharpExtensionHost();
        var published = false;
        host.Register(new TestExtension(() => published = true));
        var queue = new TurnMessageQueue();
        var context = new PiSharpExtensionContext("/workspace", queue, CancellationToken.None);

        var command = await host.ExecuteCommandAsync("/hello world", context);
        await host.PublishAsync(PiSharpExtensionEvent.AfterTurn, context);

        Assert.True(command.Handled);
        Assert.Equal("handled world", command.Message);
        Assert.Equal("transformed", host.TransformInput("original"));
        Assert.True(published);
        Assert.Empty(host.Diagnostics);
    }

    private sealed class TestExtension(Action onPublished) : IPiSharpExtension
    {
        public string Name => "test";

        public void Configure(PiSharpExtensionRegistry registry)
        {
            registry.RegisterInputTransform(input => input == "original" ? "transformed" : input);
            registry.RegisterCommand(new PiSharpCommand(
                "hello",
                "Greets the caller",
                (arguments, _) => Task.FromResult(new PiSharpCommandResult(true, $"handled {arguments}"))));
            registry.On(PiSharpExtensionEvent.AfterTurn, _ =>
            {
                onPublished();
                return Task.CompletedTask;
            });
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for files held by a test runner.
            }
        }
    }
}
