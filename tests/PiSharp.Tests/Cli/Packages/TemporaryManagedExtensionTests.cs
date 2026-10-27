using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Startup;
using PiSharp.Cli.Tui;
using PiSharp.ResourceDiscoveryExtension;
using PiSharp.Runtime.Resources;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class TemporaryManagedExtensionTests
{
    [Theory]
    [InlineData("empty", false)]
    [InlineData("omitted", false)]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    public async Task ExplicitNpmExtensionLoadsOnlyManifestEnabledExtensionsWithoutChangingPackageSettings(
        string declaration, bool expectedToLoad)
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var package = fixture.CreatePackageDirectory("temporary-extension");
        var pi = new JsonObject
        {
            ["skills"] = JsonSerializer.SerializeToNode(new[] { "skills" }),
            ["prompts"] = JsonSerializer.SerializeToNode(new[] { "prompts" }),
            ["themes"] = JsonSerializer.SerializeToNode(new[] { "themes" })
        };
        if (declaration != "omitted")
            pi["extensions"] = JsonSerializer.SerializeToNode(declaration switch
            {
                "empty" => Array.Empty<string>(),
                "valid" => new[] { "extensions/index.dll" },
                "missing" => new[] { "extensions/missing.dll" },
                _ => throw new ArgumentOutOfRangeException(nameof(declaration), declaration, null)
            });
        var packageJson = new JsonObject
        {
            ["name"] = "temporary-extension",
            ["version"] = "1.0.0",
            ["pi"] = pi
        }.ToJsonString();
        var tarball = await fixture.PackAsync(package, packageJson,
            textFiles: new Dictionary<string, string>
            {
                ["skills/guide/SKILL.md"] = "---\nname: temporary-guide\ndescription: Temporary package skill\n---\nTemporary body",
                ["prompts/review.md"] = "---\ndescription: Temporary package prompt\n---\nReview {{topic}}",
                ["themes/temporary-theme.json"] = CreateTemporaryThemeJson()
            },
            copiedFiles: new Dictionary<string, string>
            {
                ["extensions/index.dll"] = typeof(ResourceDiscoveryFixture).Assembly.Location
            });
        var source = "npm:temporary-extension@file:" + tarball;
        var loadLog = Path.Combine(fixture.Root, "extension-loads.log");
        var previousLoadLog = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
        var previousOffline = Environment.GetEnvironmentVariable("npm_config_offline");
        var previousCache = Environment.GetEnvironmentVariable("npm_config_cache");

        try
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", loadLog);
            Environment.SetEnvironmentVariable("npm_config_offline", "true");
            Environment.SetEnvironmentVariable("npm_config_cache", fixture.NpmCacheDirectory);
            var arguments = ExtensionCliPaths.Resolve(CliArguments.Parse(["--extension", source]), fixture.ProjectDirectory);
            var trust = new ProjectTrust(fixture.AgentDirectory);
            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(fixture.ProjectDirectory,
                fixture.AgentDirectory, arguments, trust, interactiveTrust: false,
                TextReader.Null, TextWriter.Null, trustedOverride: false);
            Assert.False(configuration.Trusted);
            var managedPackageRoot = Assert.Single(configuration.TemporaryExtensionPackageRoots).Path;

            using var runtime = await ProjectRuntimeContext.LoadAsync(configuration, fixture.AgentDirectory,
                arguments, configuredSessionDirectory: null);

            var loadedCliExtensions = runtime.Extensions.LoadedExtensions
                .Where(extension => extension.Source == "cli").ToArray();
            if (expectedToLoad)
            {
                var loaded = Assert.Single(loadedCliExtensions);
                Assert.Equal("temporary", loaded.Scope);
                Assert.Equal("top-level", loaded.Origin);
                Assert.Equal(managedPackageRoot, loaded.BaseDir);
                Assert.EndsWith(Path.Combine("extensions", "index.dll"), loaded.Path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            else Assert.Empty(loadedCliExtensions);
            Assert.Contains(runtime.Resources.Skills, item => item.Name == "temporary-guide");
            var skill = runtime.Resources.Skills.Single(item => item.Name == "temporary-guide");
            Assert.Equal("cli", skill.SourceInfo.Source);
            Assert.Equal("temporary", skill.SourceInfo.Scope);
            Assert.Equal("top-level", skill.SourceInfo.Origin);
            Assert.Contains(runtime.Resources.Prompts, item => item.Name == "review");
            var prompt = runtime.Resources.Prompts.Single(item => item.Name == "review");
            Assert.Equal(source, prompt.SourceInfo.Source);
            Assert.Equal("temporary", prompt.SourceInfo.Scope);
            Assert.Equal("package", prompt.SourceInfo.Origin);
            var packageFiles = ResourcePackageFiles.Load(fixture.ProjectDirectory, fixture.AgentDirectory, trusted: false,
                user: null, project: null, token: CancellationToken.None,
                temporaryPackages: configuration.TemporaryExtensionPackages,
                temporaryRoots: configuration.TemporaryExtensionPackageRoots);
            var themeFile = Assert.Single(packageFiles.TemporaryThemes);
            Assert.Equal(source, themeFile.Source.Source);
            Assert.Equal("temporary", themeFile.Source.Scope);
            Assert.Equal("package", themeFile.Source.Origin);
            if (expectedToLoad)
            {
                var extensionFile = Assert.Single(packageFiles.TemporaryExtensions);
                Assert.Equal("cli", extensionFile.Source.Source);
                Assert.Equal("temporary", extensionFile.Source.Scope);
                Assert.Equal("top-level", extensionFile.Source.Origin);
                Assert.Equal(managedPackageRoot, extensionFile.Source.BaseDir);
            }
            else Assert.Empty(packageFiles.TemporaryExtensions);
            var themes = new TerminalThemeCatalog(fixture.AgentDirectory,
                temporaryPackages: configuration.TemporaryExtensionPackages,
                temporaryPackageRoots: configuration.TemporaryExtensionPackageRoots,
                discoverThemes: false);
            Assert.Contains("temporary-theme", themes.GetAvailableNames());
            if (expectedToLoad) Assert.Equal(["configured"], await File.ReadAllLinesAsync(loadLog));
            else Assert.False(File.Exists(loadLog));
            Assert.False(File.Exists(Path.Combine(fixture.AgentDirectory, "settings.json")));
            Assert.False(File.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json")));
            Assert.True(Directory.Exists(Path.Combine(fixture.AgentDirectory, "tmp", "extensions")));
            Assert.False(Directory.Exists(Path.Combine(fixture.AgentDirectory, "npm")));
            Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "npm")));

            File.Delete(loadLog);
            var process = await RunListModelsProcessAsync(fixture.ProjectDirectory, fixture.AgentDirectory,
                fixture.NpmCacheDirectory, source, loadLog);
            Assert.True(process.ExitCode == 0, $"CLI process failed ({process.ExitCode}):\n{process.Error}\n{process.Output}");
            Assert.Equal(expectedToLoad, File.Exists(loadLog));
            if (expectedToLoad) Assert.Contains("configured", await File.ReadAllTextAsync(loadLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", previousLoadLog);
            Environment.SetEnvironmentVariable("npm_config_offline", previousOffline);
            Environment.SetEnvironmentVariable("npm_config_cache", previousCache);
        }
    }

    [Fact]
    public async Task ExplicitGitExtensionLoadsFromTemporaryManagedCacheWithoutChangingPackageSettings()
    {
        await using var fixture = new ManagedGitFixture();
        await fixture.CreateAsync();
        var environment = fixture.CreateProcessEnvironment();
        var previousEnvironment = environment.ToDictionary(pair => pair.Key,
            pair => Environment.GetEnvironmentVariable(pair.Key), StringComparer.Ordinal);
        var source = fixture.Source("v1");

        try
        {
            foreach (var (name, value) in environment) Environment.SetEnvironmentVariable(name, value);
            var arguments = ExtensionCliPaths.Resolve(CliArguments.Parse(["--extension", source]), fixture.ProjectDirectory);
            var trust = new ProjectTrust(fixture.AgentDirectory);
            using var configuration = await ProjectRuntimeConfiguration.LoadAsync(fixture.ProjectDirectory,
                fixture.AgentDirectory, arguments, trust, interactiveTrust: false,
                TextReader.Null, TextWriter.Null, trustedOverride: true);

            var packageRoot = Assert.Single(configuration.TemporaryExtensionPackageRoots);
            Assert.Equal("temporary", packageRoot.Scope);
            Assert.StartsWith(Path.Combine(fixture.AgentDirectory, "tmp", "extensions") + Path.DirectorySeparatorChar,
                packageRoot.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            using var runtime = await ProjectRuntimeContext.LoadAsync(configuration, fixture.AgentDirectory,
                arguments, configuredSessionDirectory: null);

            Assert.Contains(runtime.Extensions.LoadedExtensions, extension => extension.Source == "cli");
            var skill = Assert.Single(runtime.Resources.Skills, item => item.Name == "git-guide-v1");
            Assert.Equal("cli", skill.SourceInfo.Source);
            Assert.Equal("temporary", skill.SourceInfo.Scope);
            Assert.Equal("top-level", skill.SourceInfo.Origin);
            var prompt = Assert.Single(runtime.Resources.Prompts, item => item.Name == "review");
            Assert.Equal(source, prompt.SourceInfo.Source);
            Assert.Equal("temporary", prompt.SourceInfo.Scope);
            Assert.Equal("package", prompt.SourceInfo.Origin);
            Assert.Equal("Review from Git v1.", prompt.Template);
            var packageFiles = ResourcePackageFiles.Load(fixture.ProjectDirectory, fixture.AgentDirectory, trusted: false,
                user: null, project: null, token: CancellationToken.None,
                temporaryPackages: configuration.TemporaryExtensionPackages,
                temporaryRoots: configuration.TemporaryExtensionPackageRoots);
            var themeFile = Assert.Single(packageFiles.TemporaryThemes);
            Assert.Equal(source, themeFile.Source.Source);
            Assert.Equal("temporary", themeFile.Source.Scope);
            Assert.Equal("package", themeFile.Source.Origin);
            var extensionFile = Assert.Single(packageFiles.TemporaryExtensions);
            Assert.Equal("cli", extensionFile.Source.Source);
            Assert.Equal("temporary", extensionFile.Source.Scope);
            Assert.Equal("top-level", extensionFile.Source.Origin);
            var themes = new TerminalThemeCatalog(fixture.AgentDirectory,
                temporaryPackages: configuration.TemporaryExtensionPackages,
                temporaryPackageRoots: configuration.TemporaryExtensionPackageRoots,
                discoverThemes: false);
            Assert.Contains("managed-git-theme", themes.GetAvailableNames());
            Assert.Single(await File.ReadAllLinesAsync(fixture.PackageInstallLog));
            Assert.Single(await File.ReadAllLinesAsync(fixture.DependencyInstallLog));
            Assert.False(File.Exists(Path.Combine(fixture.AgentDirectory, "settings.json")));
            Assert.False(File.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(fixture.AgentDirectory, "git")));
            Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "git")));

            File.Delete(fixture.ExtensionLoadLog);
            var process = await RunListModelsProcessAsync(fixture.ProjectDirectory, fixture.AgentDirectory,
                fixture.NpmCacheDirectory, source, fixture.ExtensionLoadLog, environment);
            Assert.True(process.ExitCode == 0, $"CLI process failed ({process.ExitCode}):\n{process.Error}\n{process.Output}");
            Assert.Contains("configured", await File.ReadAllTextAsync(fixture.ExtensionLoadLog));
            Assert.Single(await File.ReadAllLinesAsync(fixture.PackageInstallLog));
            Assert.Single(await File.ReadAllLinesAsync(fixture.DependencyInstallLog));
        }
        finally
        {
            foreach (var (name, value) in previousEnvironment) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task TemporaryNpmAndGitRootsRetainExplicitSourceOrder()
    {
        await using var npmFixture = new ManagedNpmFixture();
        await using var gitFixture = new ManagedGitFixture();
        npmFixture.Create();
        await gitFixture.CreateAsync();
        var npmDirectory = npmFixture.CreatePackageDirectory("temporary-order-npm");
        var tarball = await npmFixture.PackAsync(npmDirectory,
            "{\"name\":\"temporary-order-npm\",\"version\":\"1.0.0\",\"scripts\":{\"postinstall\":\"node postinstall.js\"}}",
            textFiles: new Dictionary<string, string>
            {
                ["postinstall.js"] = "require('node:fs').appendFileSync(process.env.PISHARP_TEST_GIT_PACKAGE_LOG, 'npm\\n');"
            });
        var npmSource = "npm:temporary-order-npm@file:" + tarball;
        var gitSource = gitFixture.Source("v1");
        var environment = gitFixture.CreateProcessEnvironment();
        environment["npm_config_cache"] = npmFixture.NpmCacheDirectory;
        var acquisitionLog = Path.Combine(npmFixture.Root, "acquisition-order.log");
        environment["PISHARP_TEST_GIT_PACKAGE_LOG"] = acquisitionLog;
        var store = new ManagedResourcePackageStore(npmFixture.AgentDirectory, environment);

        var roots = await store.ResolveTemporaryExtensionSourcesAsync(npmFixture.ProjectDirectory,
            [gitSource, npmSource], offline: false);

        Assert.Equal([gitSource, npmSource], roots.Select(root => root.Source).ToArray());
        Assert.All(roots, root => Assert.Equal("temporary", root.Scope));
        Assert.Equal(["run", "npm"], await File.ReadAllLinesAsync(acquisitionLog));
    }

    [Fact]
    public async Task TemporaryGitRepositoriesWithSameHostPathAndRefUseIsolatedCaches()
    {
        await using var first = new ManagedGitFixture();
        await using var second = new ManagedGitFixture();
        await first.CreateAsync();
        await second.CreateAsync();
        await first.SetBranchAsync("same-ref", first.Version1);
        await second.SetBranchAsync("same-ref", second.Version2);
        var firstSource = first.Source("same-ref");
        var secondSource = second.Source("same-ref");
        Assert.NotEqual(firstSource, secondSource);

        var environment = first.CreateProcessEnvironment();
        var store = new ManagedResourcePackageStore(first.AgentDirectory, environment);
        var roots = await store.ResolveTemporaryExtensionSourcesAsync(first.ProjectDirectory,
            [firstSource, secondSource], offline: false);

        Assert.Equal([firstSource, secondSource], roots.Select(root => root.Source).ToArray());
        var firstRoot = Assert.Single(roots, root => root.Source == firstSource).Path;
        var secondRoot = Assert.Single(roots, root => root.Source == secondSource).Path;
        Assert.NotEqual(firstRoot, secondRoot);
        Assert.Equal(first.Version1, await ReadGitHeadAsync(firstRoot));
        Assert.Equal(second.Version2, await ReadGitHeadAsync(secondRoot));
        Assert.Contains("git-guide-v1", await File.ReadAllTextAsync(
            Path.Combine(firstRoot, "skills", "git-guide-v1", "SKILL.md")), StringComparison.Ordinal);
        Assert.Contains("git-guide-v2", await File.ReadAllTextAsync(
            Path.Combine(secondRoot, "skills", "git-guide-v2", "SKILL.md")), StringComparison.Ordinal);

        var offlineRoots = await store.ResolveTemporaryExtensionSourcesAsync(first.ProjectDirectory,
            [firstSource, secondSource], offline: true);
        Assert.Equal([firstRoot, secondRoot], offlineRoots.Select(root => root.Path).ToArray());
        Assert.Equal(first.Version1, await ReadGitHeadAsync(firstRoot));
        Assert.Equal(second.Version2, await ReadGitHeadAsync(secondRoot));
    }

    [Fact]
    public async Task TemporaryGitCacheSeparatesCollidingShortHashesAndRejectsMismatchedMetadata()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var fixture = new ManagedGitFixture();
        await fixture.CreateAsync();
        await fixture.SetBranchAsync("branch015869", fixture.Version1);
        await fixture.SetBranchAsync("branch033708", fixture.Version2);
        var sourceA = "git:localhost/fixture/managed@branch015869";
        var sourceB = "git:localhost/fixture/managed@branch033708";
        static string OldShortHash(string source)
        {
            Assert.True(ManagedResourcePackageStore.TryGetGitIdentity(source, out var identity));
            var git = source[4..];
            var at = git.LastIndexOf('@');
            var reference = git[(at + 1)..];
            var path = identity["git:localhost/".Length..];
            var key = "git-localhost-" + path + "@" + reference;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..8];
        }
        Assert.Equal("33040f3c", OldShortHash(sourceA));
        Assert.Equal(OldShortHash(sourceA), OldShortHash(sourceB));

        var environment = fixture.CreateProcessEnvironment();
        var globalConfig = environment["GIT_CONFIG_GLOBAL"]!;
        await File.WriteAllTextAsync(globalConfig,
            $"[url \"git://127.0.0.1:{fixture.Port}/fixture/repo.git\"]\n" +
            "\tinsteadOf = https://localhost/fixture/managed\n");
        var store = new ManagedResourcePackageStore(fixture.AgentDirectory, environment);
        var roots = await store.ResolveTemporaryExtensionSourcesAsync(fixture.ProjectDirectory,
            [sourceA, sourceB], offline: false);
        Assert.Equal([sourceA, sourceB], roots.Select(root => root.Source).ToArray());
        var rootA = Assert.Single(roots, root => root.Source == sourceA).Path;
        var rootB = Assert.Single(roots, root => root.Source == sourceB).Path;
        Assert.NotEqual(rootA, rootB);
        Assert.Equal(fixture.Version1, await ReadGitHeadAsync(rootA));
        Assert.Equal(fixture.Version2, await ReadGitHeadAsync(rootB));

        await SetGitConfigAsync(rootB, "pisharp.source-ref", "branch015869");
        Assert.Empty(await store.ResolveTemporaryExtensionSourcesAsync(fixture.ProjectDirectory,
            [sourceB], offline: true));
        var repaired = await store.ResolveTemporaryExtensionSourcesAsync(fixture.ProjectDirectory,
            [sourceB], offline: false);
        Assert.Equal(rootB, Assert.Single(repaired).Path);
        Assert.Equal(fixture.Version2, await ReadGitHeadAsync(rootB));
    }

    private static async Task<string> ReadGitHeadAsync(string path)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("rev-parse");
        process.StartInfo.ArgumentList.Add("HEAD");
        if (!process.Start()) throw new IOException("Could not start git rev-parse.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error);
        return (await output).Trim();
    }

    private static async Task SetGitConfigAsync(string repository, string key, string value)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repository,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repository);
        process.StartInfo.ArgumentList.Add("config");
        process.StartInfo.ArgumentList.Add("--local");
        process.StartInfo.ArgumentList.Add(key);
        process.StartInfo.ArgumentList.Add(value);
        if (!process.Start()) throw new IOException("Could not start git config.");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error);
    }

    private static string CreateTemporaryThemeJson()
    {
        using var stream = typeof(TerminalThemeCatalog).Assembly.GetManifestResourceStream("PiSharp.Cli.Tui.Themes.dark.json")
            ?? throw new InvalidOperationException("Built-in theme fixture resource is missing.");
        var theme = JsonNode.Parse(stream) ?? throw new InvalidDataException("Built-in theme fixture is invalid.");
        theme["name"] = "temporary-theme";
        return theme.ToJsonString();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunListModelsProcessAsync(
        string projectDirectory, string agentDirectory, string npmCacheDirectory, string source, string loadLog,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in new[] { "--list-models", "--offline", "--extension", source })
            start.ArgumentList.Add(argument);
        start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
        start.Environment["PISHARP_TEST_EXTENSION_LOAD_LOG"] = loadLog;
        start.Environment["npm_config_offline"] = "true";
        start.Environment["npm_config_cache"] = npmCacheDirectory;
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>()) start.Environment[name] = value;
        foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL",
                     "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH" })
            start.Environment.Remove(name);

        using var process = Process.Start(start) ?? throw new IOException("Could not start the PiSharp CLI process.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await outputTask, await errorTask);
    }
}
