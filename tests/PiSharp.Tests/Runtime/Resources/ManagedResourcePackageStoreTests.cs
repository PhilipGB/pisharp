using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Tests.TestInfrastructure;
using PiSharp.ResourceDiscoveryExtension;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class ManagedResourcePackageStoreTests
{
    [Fact]
    public async Task LocalTarballsLoadResourcesAndNativeExtensionOncePerStartupAndReload()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var previousOffline = Environment.GetEnvironmentVariable("npm_config_offline");
        var previousNpmCache = Environment.GetEnvironmentVariable("npm_config_cache");
        var previousDiscovery = Environment.GetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY");
        var previousLoadLog = Environment.GetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG");
        var skillInstallLog = Path.Combine(fixture.Root, "skill-install.log");
        var dependencyInstallLog = Path.Combine(fixture.Root, "dependency-install.log");
        var extensionInstallLog = Path.Combine(fixture.Root, "extension-install.log");
        var extensionLoadLog = Path.Combine(fixture.Root, "extension-load.log");

        try
        {
            Environment.SetEnvironmentVariable("npm_config_offline", "true");
            Environment.SetEnvironmentVariable("npm_config_cache", fixture.NpmCacheDirectory);
            Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", "true");
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", extensionLoadLog);

            var skillLogScript = AppendLogScript(skillInstallLog);
            var skillPackageDirectory = fixture.CreatePackageDirectory("managed-skill");
            var skillTheme = ReadBuiltInTheme("managed-package-theme");
            var skillTarball = await fixture.PackAsync(skillPackageDirectory, JsonSerializer.Serialize(new
            {
                name = "managed-skill",
                version = "1.2.3",
                pi = new { skills = new[] { "skills" }, prompts = new[] { "prompts" }, themes = new[] { "themes" } },
                scripts = new { postinstall = "node postinstall.js" }
            }), new Dictionary<string, string>
            {
                ["skills/guide/SKILL.md"] = "---\nname: managed-guide\ndescription: Installed skill\n---\nUse the installed guide.",
                ["prompts/review.md"] = "Review installed package.",
                ["themes/managed.json"] = skillTheme,
                ["postinstall.js"] = skillLogScript
            });
            var skillSource = "\uFEFF \tnpm:managed-skill@file:" + skillTarball + "\r\n\uFEFF";

            var dependencyPackageDirectory = fixture.CreatePackageDirectory("managed-fixture-dependency");
            var dependencyTarball = await fixture.PackAsync(dependencyPackageDirectory, JsonSerializer.Serialize(new
            {
                name = "managed-fixture-dependency",
                version = "1.0.0",
                scripts = new { postinstall = "node postinstall.js" }
            }), new Dictionary<string, string> { ["postinstall.js"] = AppendLogScript(dependencyInstallLog) });

            var extensionPackageDirectory = fixture.CreatePackageDirectory("managed-native-extension");
            var extensionTarball = await fixture.PackAsync(extensionPackageDirectory, JsonSerializer.Serialize(new
            {
                name = "managed-native-extension",
                version = "1.0.0",
                pi = new { extensions = new[] { "extensions/resource-discovery.dll" } },
                dependencies = new Dictionary<string, string>
                {
                    ["managed-fixture-dependency"] = "file:" + dependencyTarball
                },
                peerDependencies = new Dictionary<string, string>
                {
                    ["pisharp-host-api-must-not-install"] = "^1.0.0"
                },
                scripts = new { postinstall = "node postinstall.js" }
            }), new Dictionary<string, string>
            {
                ["postinstall.js"] = AppendLogScript(extensionInstallLog)
            }, new Dictionary<string, string>
            {
                ["extensions/resource-discovery.dll"] = typeof(ResourceDiscoveryFixture).Assembly.Location
            });
            var extensionSource = "npm:managed-native-extension@file:" + extensionTarball;
            await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"),
                JsonSerializer.Serialize(new { packages = new[] { skillSource, extensionSource } }));

            var startupSkill = "startup-package-skill";
            var reloadedSkill = "reloaded-package-skill";
            await WriteExtensionSkillAsync(fixture.ProjectDirectory, "startup", startupSkill);
            await WriteExtensionSkillAsync(fixture.ProjectDirectory, "reloaded", reloadedSkill);

            var projectDeltaSource = "npm:managed-skill@file:" + Path.Combine(fixture.Root, "missing-project-copy.tgz");
            Directory.CreateDirectory(Path.Combine(fixture.ProjectDirectory, ".pi"));
            await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json"),
                JsonSerializer.Serialize(new
                {
                    packages = new[]
                    {
                        new { source = projectDeltaSource, autoload = false, skills = new[] { "skills/**/*.md" } }
                    }
                }));

            var arguments = CliArguments.Parse([]);
            using (var configuration = await LoadConfigurationAsync(fixture, arguments))
            using (var runtime = await ProjectRuntimeContext.LoadAsync(configuration, fixture.AgentDirectory, arguments,
                       configuredSessionDirectory: null, resourceDiscoveryReason: ExtensionResourceDiscoveryReason.Startup))
            {
                var skill = Assert.Single(runtime.Resources.Skills, item => item.Name == "managed-guide");
                Assert.Equal(projectDeltaSource, skill.SourceInfo.Source);
                Assert.Equal("project", skill.SourceInfo.Scope);
                Assert.Equal(Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "managed-skill"), skill.SourceInfo.BaseDir);
                Assert.Contains(runtime.Resources.Prompts, item => item.Name == "review");
                Assert.Contains(runtime.Resources.Skills, item => item.Name == startupSkill);
                Assert.Single(runtime.Extensions.LoadedExtensions);
                Assert.Single(runtime.ExtensionResources.SkillPaths);

                var themes = new TerminalThemeCatalog(fixture.AgentDirectory, fixture.ProjectDirectory,
                    environment: _ => null, trueColorOverride: true,
                    userPackages: configuration.BaseUserSettings.Packages,
                    projectPackages: configuration.ProjectSettings?.Packages,
                    managedPackageRoots: configuration.ManagedPackageRoots);
                Assert.Contains("managed-package-theme", themes.GetAvailableNames());
            }

            using (var configuration = await LoadConfigurationAsync(fixture, arguments))
            using (var runtime = await ProjectRuntimeContext.LoadAsync(configuration, fixture.AgentDirectory, arguments,
                       configuredSessionDirectory: null, resourceDiscoveryReason: ExtensionResourceDiscoveryReason.Reload))
            {
                Assert.Contains(runtime.Resources.Skills, item => item.Name == "managed-guide");
                Assert.Contains(runtime.Resources.Skills, item => item.Name == reloadedSkill);
                Assert.Single(runtime.Extensions.LoadedExtensions);
            }

            Assert.Equal(["run"], await File.ReadAllLinesAsync(skillInstallLog));
            Assert.Equal(["run"], await File.ReadAllLinesAsync(dependencyInstallLog));
            Assert.Equal(["run"], await File.ReadAllLinesAsync(extensionInstallLog));
            Assert.Equal(["configured", "configured"], await File.ReadAllLinesAsync(extensionLoadLog));
            Assert.True(Directory.Exists(Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "managed-fixture-dependency")));
            Assert.False(Directory.Exists(Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "pisharp-host-api-must-not-install")));
            Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "npm", "node_modules", "managed-skill")));
            Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "npm", "node_modules", "managed-native-extension")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("npm_config_offline", previousOffline);
            Environment.SetEnvironmentVariable("npm_config_cache", previousNpmCache);
            Environment.SetEnvironmentVariable("PISHARP_TEST_RESOURCE_DISCOVERY", previousDiscovery);
            Environment.SetEnvironmentVariable("PISHARP_TEST_EXTENSION_LOAD_LOG", previousLoadLog);
        }
    }

    [Fact]
    public async Task OfflineAndUntrustedResolutionDoNotInstallMissingPackages()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var source = new ResourcePackageSource("npm:managed-missing@file:" + Path.Combine(fixture.Root, "missing.tgz"));
        var store = new ManagedResourcePackageStore(fixture.AgentDirectory,
            new Dictionary<string, string?> { ["npm_config_offline"] = "true", ["npm_config_cache"] = fixture.NpmCacheDirectory });

        var offline = await store.ResolveAsync(fixture.ProjectDirectory, trusted: true,
            userPackages: [source], offline: true);
        var untrusted = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false,
            projectPackages: [source]);

        Assert.Empty(offline);
        Assert.Empty(untrusted);
        Assert.False(Directory.Exists(Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "managed-missing")));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "npm")));
    }

    [Fact]
    public async Task NpmOwnsConfiguredRangeChecksForCompatibleAndIncompatibleCachedVersions()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var packageDirectory = fixture.CreatePackageDirectory("range-package");
        var tarball = await fixture.PackAsync(packageDirectory, "{\"name\":\"range-package\",\"version\":\"1.4.2\"}");
        var store = new ManagedResourcePackageStore(fixture.AgentDirectory,
            new Dictionary<string, string?> { ["npm_config_offline"] = "true", ["npm_config_cache"] = fixture.NpmCacheDirectory });
        var installed = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new("npm:range-package@file:" + tarball)]);
        var compatible = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new("npm:range-package@^1.2.0")]);
        var incompatible = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new("npm:range-package@>=2.0.0")], offline: true);
        var distTag = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new("npm:range-package@beta1")], offline: true);
        var numberedDistTag = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new("npm:range-package@next-2")], offline: true);
        var newerPackageDirectory = fixture.CreatePackageDirectory("range-package-newer");
        var newerTarball = await fixture.PackAsync(newerPackageDirectory,
            "{\"name\":\"range-package\",\"version\":\"1.8.0\"}");
        var duplicateUserSources = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new("npm:range-package@file:" + tarball), new("npm:range-package@file:" + newerTarball)],
            offline: true);
        var projectDeltaSource = "npm:range-package@file:" + Path.Combine(fixture.Root, "missing-project-copy.tgz");
        var deltaRoots = await store.ResolveAsync(fixture.ProjectDirectory, trusted: true,
            userPackages: [new("npm:range-package@^1.2.0")],
            projectPackages: [new(projectDeltaSource, Skills: ["skills/**/*.md"], Autoload: false, HasFilter: true)],
            offline: true);

        Assert.Single(installed);
        Assert.Single(compatible);
        Assert.Empty(incompatible);
        Assert.Single(distTag);
        Assert.Single(numberedDistTag);
        Assert.Single(duplicateUserSources);
        Assert.Equal("npm:range-package@file:" + tarball, duplicateUserSources[0].Source);
        using (var installedManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
                   fixture.AgentDirectory, "npm", "node_modules", "range-package", "package.json"))))
            Assert.Equal("1.4.2", installedManifest.RootElement.GetProperty("version").GetString());
        var deltaRoot = Assert.Single(deltaRoots, root => root.Scope == "project");
        Assert.Equal(projectDeltaSource, deltaRoot.Source);
        Assert.Equal(Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "range-package"), deltaRoot.Path);
    }

    [Fact]
    public async Task CancellingNpmInstallKillsAndAwaitsLifecycleChildWithoutRetry()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var pidPath = Path.Combine(fixture.Root, "lifecycle.pid");
        var packageDirectory = fixture.CreatePackageDirectory("slow-package");
        var script = $"require('node:fs').writeFileSync({JsonSerializer.Serialize(pidPath)}, String(process.pid));\nsetInterval(() => {{}}, 1000);\n";
        var tarball = await fixture.PackAsync(packageDirectory, JsonSerializer.Serialize(new
        {
            name = "slow-package",
            version = "1.0.0",
            scripts = new { postinstall = "node postinstall.js" }
        }), new Dictionary<string, string> { ["postinstall.js"] = script });
        var store = new ManagedResourcePackageStore(fixture.AgentDirectory,
            new Dictionary<string, string?> { ["npm_config_offline"] = "true", ["npm_config_cache"] = fixture.NpmCacheDirectory });
        using var cancellation = new CancellationTokenSource();
        var install = store.ResolveAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new("npm:slow-package@file:" + tarball)], cancellationToken: cancellation.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!File.Exists(pidPath) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(File.Exists(pidPath), "npm did not start the package lifecycle script.");
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.True(await WaitForProcessExitAsync(pid), "The npm lifecycle process remained alive after cancellation.");
        Assert.Equal(pid.ToString(), await File.ReadAllTextAsync(pidPath));
    }

    private static async Task<ProjectRuntimeConfiguration> LoadConfigurationAsync(ManagedNpmFixture fixture,
        CliArguments arguments) => await ProjectRuntimeConfiguration.LoadAsync(fixture.ProjectDirectory,
        fixture.AgentDirectory, arguments, new ProjectTrust(fixture.AgentDirectory), interactiveTrust: false,
        TextReader.Null, TextWriter.Null, trustedOverride: true);

    private static string AppendLogScript(string path) =>
        $"require('node:fs').appendFileSync({JsonSerializer.Serialize(path)}, {JsonSerializer.Serialize("run\n")});\n";

    private static string ReadBuiltInTheme(string name)
    {
        using var resource = typeof(TerminalTheme).Assembly.GetManifestResourceStream("PiSharp.Cli.Tui.Themes.dark.json");
        Assert.NotNull(resource);
        using var reader = new StreamReader(resource!);
        var theme = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        theme["name"] = name;
        return theme.ToJsonString();
    }

    private static async Task WriteExtensionSkillAsync(string projectDirectory, string phase, string name)
    {
        var path = Path.Combine(projectDirectory, ".pi", "extension-resources", phase, "skills", name, "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $"---\nname: {name}\ndescription: Discovered extension skill\n---\nUse {name}.");
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        return false;
    }
}
