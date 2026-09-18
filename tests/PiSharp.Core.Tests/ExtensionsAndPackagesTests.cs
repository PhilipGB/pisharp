using PiSharp.Cli;
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
    public async Task AgentFactoryDoesNotLoadProjectExtensionBeforeTrust()
    {
        using var workspace = new TemporaryDirectory();
        var projectExtensionDirectory = Path.Combine(workspace.Path, ".pi", "extensions");
        var home = Path.Combine(workspace.Path, "home");
        var globalExtensionDirectory = Path.Combine(home, ".pi", "agent", "extensions");
        Directory.CreateDirectory(projectExtensionDirectory);
        Directory.CreateDirectory(globalExtensionDirectory);
        var projectExtension = Path.Combine(projectExtensionDirectory, "project-extension.dll");
        var globalExtension = Path.Combine(globalExtensionDirectory, "global-extension.dll");
        File.Copy(typeof(ProjectTrustTestExtension).Assembly.Location, projectExtension);
        File.Copy(typeof(ProjectTrustTestExtension).Assembly.Location, globalExtension);
        var options = CliOptions.Parse([
            "--model", "test-model",
            "--endpoint", "http://localhost:8000/v1",
            "--cwd", workspace.Path,
            "--no-session",
        ]);

        var (runtime, state, _) = await ModelRuntimeTestKit.CreateAsync();
        var untrusted = await AgentFactory.CreateAsync(
            options, projectTrusted: false, CancellationToken.None, runtime, state, home);
        Assert.True(
            untrusted.ExtensionHost.LoadedPaths.Any(path => path == Path.GetFullPath(globalExtension)),
            $"Loaded: {string.Join(",", untrusted.ExtensionHost.LoadedPaths)} Diagnostics: {string.Join(";", untrusted.ExtensionHost.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        Assert.DoesNotContain(untrusted.ExtensionHost.LoadedPaths, path => path == Path.GetFullPath(projectExtension));

        using var trustedWorkspace = new TemporaryDirectory();
        var trustedProjectExtensionDirectory = Path.Combine(trustedWorkspace.Path, ".pi", "extensions");
        Directory.CreateDirectory(trustedProjectExtensionDirectory);
        var trustedProjectExtension = Path.Combine(trustedProjectExtensionDirectory, "project-extension.dll");
        File.Copy(typeof(ProjectTrustTestExtension).Assembly.Location, trustedProjectExtension);
        var trustedOptions = CliOptions.Parse([
            "--model", "test-model",
            "--endpoint", "http://localhost:8000/v1",
            "--cwd", trustedWorkspace.Path,
            "--no-session",
        ]);
        var (runtime2, state2, _) = await ModelRuntimeTestKit.CreateAsync();
        var trusted = await AgentFactory.CreateAsync(
            trustedOptions,
            projectTrusted: true,
            CancellationToken.None,
            runtime2,
            state2,
            Path.Combine(trustedWorkspace.Path, "home"));
        Assert.Contains(trusted.ExtensionHost.LoadedPaths, path => path == Path.GetFullPath(trustedProjectExtension));

        using var packageWorkspace = new TemporaryDirectory();
        var packageDirectory = Path.Combine(packageWorkspace.Path, ".pi", "packages", "project-package");
        Directory.CreateDirectory(packageDirectory);
        var packageExtension = Path.Combine(packageDirectory, "project-package.dll");
        File.Copy(typeof(ProjectTrustTestExtension).Assembly.Location, packageExtension);
        File.WriteAllText(
            Path.Combine(packageDirectory, "package.json"),
            "{\"name\":\"project-package\",\"pi\":{\"extensions\":[\"project-package.dll\"]}}");
        var packageOptions = CliOptions.Parse([
            "--model", "test-model",
            "--endpoint", "http://localhost:8000/v1",
            "--cwd", packageWorkspace.Path,
            "--no-session",
        ]);
        var (runtime3, state3, _) = await ModelRuntimeTestKit.CreateAsync();
        var untrustedPackage = await AgentFactory.CreateAsync(
            packageOptions,
            projectTrusted: false,
            CancellationToken.None,
            runtime3,
            state3,
            Path.Combine(packageWorkspace.Path, "home"));
        Assert.DoesNotContain(untrustedPackage.ExtensionHost.LoadedPaths, path => path == Path.GetFullPath(packageExtension));

        var explicitOptions = CliOptions.Parse([
            "--model", "test-model",
            "--endpoint", "http://localhost:8000/v1",
            "--cwd", packageWorkspace.Path,
            "--no-session",
            "--extension", packageExtension,
        ]);
        var (runtime4, state4, _) = await ModelRuntimeTestKit.CreateAsync();
        var explicitlyAuthorized = await AgentFactory.CreateAsync(
            explicitOptions,
            projectTrusted: false,
            CancellationToken.None,
            runtime4,
            state4,
            Path.Combine(packageWorkspace.Path, "home"));
        Assert.Contains(explicitlyAuthorized.ExtensionHost.LoadedPaths, path => path == Path.GetFullPath(packageExtension));
    }

    [Fact]
    public void PackageDiscoveryPreservesUserAndProjectOrigins()
    {
        using var workspace = new TemporaryDirectory();
        var userPackage = Path.Combine(workspace.Path, "home", ".pi", "agent", "packages", "user");
        var projectPackage = Path.Combine(workspace.Path, ".pi", "packages", "project");
        Directory.CreateDirectory(userPackage);
        Directory.CreateDirectory(projectPackage);
        File.WriteAllText(Path.Combine(userPackage, "package.json"), "{\"name\":\"user\",\"pi\":{\"extensions\":[\"user.dll\"]}}");
        File.WriteAllText(Path.Combine(projectPackage, "package.json"), "{\"name\":\"project\",\"pi\":{\"extensions\":[\"project.dll\"]}}");

        var result = new PiPackageCatalog().Discover(workspace.Path, Path.Combine(workspace.Path, "home"));

        Assert.Equal(PiPackageScope.User, Assert.Single(result.Packages, package => package.Name == "user").Scope);
        Assert.Equal(PiPackageScope.Project, Assert.Single(result.Packages, package => package.Name == "project").Scope);
        Assert.Contains(Path.Combine(userPackage, "user.dll"), result.UserExtensionPaths);
        Assert.Contains(Path.Combine(projectPackage, "project.dll"), result.ProjectExtensionPaths);
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

    private sealed class TestExtension : IPiSharpExtension
    {
        private readonly Action _onPublished;

        public TestExtension() : this(() => { })
        {
        }

        public TestExtension(Action onPublished) => _onPublished = onPublished;

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
                _onPublished();
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

public sealed class ProjectTrustTestExtension : IPiSharpExtension
{
    public const string CommandName = "project-trust-test-extension";

    public string Name => CommandName;

    public void Configure(PiSharpExtensionRegistry registry)
    {
        registry.RegisterCommand(new PiSharpCommand(
            CommandName,
            "Test extension loaded only after trust.",
            (_, _) => Task.FromResult(new PiSharpCommandResult(true, "loaded"))));
    }
}
