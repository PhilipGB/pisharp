using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Resources;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class ResourcePackageCommandProcessTests
{
    [Fact]
    public async Task NativeNupkgInstallUpdateAndRemovePersistVersionedRootsOffline()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var v1Path = Path.Combine(fixture.Root, "native-fixture-1.0.0.nupkg");
        var brokenV2Path = Path.Combine(fixture.Root, "native-fixture-2.0.0-broken.nupkg");
        var incompatibleV2Path = Path.Combine(fixture.Root, "native-fixture-2.0.0-incompatible.nupkg");
        var v2Path = Path.Combine(fixture.Root, "native-fixture-2.0.0.nupkg");
        await CreateNativeNupkgAsync(v1Path, "example.native-fixture", "1.0.0", includeAssembly: true);
        await CreateNativeNupkgAsync(brokenV2Path, "example.native-fixture", "2.0.0", includeAssembly: false);
        await CreateNativeNupkgAsync(incompatibleV2Path, "example.native-fixture", "2.0.0", includeAssembly: true, apiVersion: 2);
        await CreateNativeNupkgAsync(v2Path, "example.native-fixture", "2.0.0", includeAssembly: true);

        var installed = await RunAsync(fixture, "install", v1Path);
        Assert.True(installed.ExitCode == 0, installed.Error + "\n" + installed.Output);
        Assert.Contains("Installed PiSharp-native extension example.native-fixture 1.0.0", installed.Output);
        var settingsPath = Path.Combine(fixture.AgentDirectory, "settings.json");
        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath)))
        {
            var package = Assert.Single(settings.RootElement.GetProperty("nativeExtensions").EnumerateArray());
            Assert.Equal("example.native-fixture", package.GetProperty("id").GetString());
            Assert.Equal("1.0.0", package.GetProperty("version").GetString());
        }

        var v1Root = Path.Combine(fixture.AgentDirectory, "native-extensions", "example.native-fixture", "1.0.0");
        Assert.True(File.Exists(Path.Combine(v1Root, "pisharp-extension.json")));
        Assert.True(File.Exists(Path.Combine(v1Root, "lib", "net10.0", "Example.NativeFixture.dll")));
        var brokenUpdate = await RunAsync(fixture, "update", "--extension", brokenV2Path);
        Assert.Equal(1, brokenUpdate.ExitCode);
        Assert.Contains("assembly is missing", brokenUpdate.Error);
        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath)))
            Assert.Equal("1.0.0", settings.RootElement.GetProperty("nativeExtensions")[0].GetProperty("version").GetString());
        Assert.False(Directory.Exists(Path.Combine(fixture.AgentDirectory, "native-extensions", "example.native-fixture", "2.0.0")));

        var incompatibleUpdate = await RunAsync(fixture, "update", "--extension", incompatibleV2Path);
        Assert.Equal(1, incompatibleUpdate.ExitCode);
        Assert.Contains("supports v1", incompatibleUpdate.Error);
        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath)))
            Assert.Equal("1.0.0", settings.RootElement.GetProperty("nativeExtensions")[0].GetProperty("version").GetString());

        var updated = await RunAsync(fixture, "update", "--extension", v2Path);
        Assert.True(updated.ExitCode == 0, updated.Error + "\n" + updated.Output);
        Assert.Contains("Updated PiSharp-native extension example.native-fixture 2.0.0", updated.Output);
        var v2Root = Path.Combine(fixture.AgentDirectory, "native-extensions", "example.native-fixture", "2.0.0");
        Assert.True(File.Exists(Path.Combine(v2Root, "pisharp-extension.json")));
        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath)))
            Assert.Equal("2.0.0", settings.RootElement.GetProperty("nativeExtensions")[0].GetProperty("version").GetString());

        var listed = await RunAsync(fixture, "list");
        Assert.Equal(0, listed.ExitCode);
        Assert.Contains("User PiSharp-native compiled extensions:", listed.Output);
        Assert.Contains("example.native-fixture 2.0.0", listed.Output);
        Assert.Contains(v2Root, listed.Output);

        var removed = await RunAsync(fixture, "remove", "example.native-fixture");
        Assert.True(removed.ExitCode == 0, removed.Error + "\n" + removed.Output);
        Assert.False(Directory.Exists(Path.Combine(fixture.AgentDirectory, "native-extensions", "example.native-fixture")));
        using var afterRemoval = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        Assert.Empty(afterRemoval.RootElement.GetProperty("nativeExtensions").EnumerateArray());
    }

    [Fact]
    public async Task ProjectNativeNupkgInstallUsesProjectScopeAndRequiresApproval()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var packagePath = Path.Combine(fixture.Root, "project-native.nupkg");
        await CreateNativeNupkgAsync(packagePath, "example.project-native", "1.0.0", includeAssembly: true);

        var installed = await RunAsync(fixture, "install", "--local", "--approve", packagePath);
        Assert.True(installed.ExitCode == 0, installed.Error + "\n" + installed.Output);
        var projectSettingsPath = Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json");
        var projectRoot = Path.Combine(fixture.ProjectDirectory, ".pi", "native-extensions", "example.project-native", "1.0.0");
        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(projectSettingsPath)))
        {
            var configured = settings.RootElement.GetProperty("nativeExtensions")[0];
            Assert.Equal("example.project-native", configured.GetProperty("id").GetString());
            var source = configured.GetProperty("source").GetString()!;
            var roots = await new NativeExtensionPackageStore(fixture.AgentDirectory).ResolveAsync(
                fixture.ProjectDirectory, trusted: false,
                projectSources: [new("example.project-native", "1.0.0", source)]);
            var root = Assert.Single(roots);
            Assert.Equal("project", root.Scope);
            Assert.Equal(projectRoot, root.Path);
        }
        Assert.True(File.Exists(Path.Combine(projectRoot, "pisharp-extension.json")));

        var removedWithoutTrust = await RunAsync(fixture, "remove", "--local", "--no-approve", "example.project-native");
        Assert.Equal(1, removedWithoutTrust.ExitCode);
        Assert.Contains("Project is not trusted", removedWithoutTrust.Error);
        Assert.True(Directory.Exists(projectRoot));

        var removed = await RunAsync(fixture, "remove", "--local", "--approve", "example.project-native");
        Assert.True(removed.ExitCode == 0, removed.Error + "\n" + removed.Output);
        Assert.False(Directory.Exists(projectRoot));
    }

    [Fact]
    public async Task LocalPackageInstallListAndRemovePreserveReferencesAndScopes()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var userLocal = Path.Combine(fixture.ProjectDirectory, "user-local");
        var projectLocal = Path.Combine(fixture.ProjectDirectory, "project-local");
        Directory.CreateDirectory(userLocal);
        Directory.CreateDirectory(projectLocal);

        var installed = await RunAsync(fixture, "install", "./user-local");
        Assert.Equal(0, installed.ExitCode);
        var userSettingsPath = Path.Combine(fixture.AgentDirectory, "settings.json");
        using (var userSettings = JsonDocument.Parse(await File.ReadAllTextAsync(userSettingsPath)))
        {
            var source = userSettings.RootElement.GetProperty("packages")[0].GetString();
            Assert.Equal(Path.GetRelativePath(fixture.AgentDirectory, userLocal), source);
        }

        var listed = await RunAsync(fixture, "list", "ignored-first-argument");
        Assert.Equal(0, listed.ExitCode);
        Assert.Contains("User packages:", listed.Output);
        Assert.Contains(userLocal, listed.Output);

        var projectInstall = await RunAsync(fixture, "install", "--local", "--approve", "./project-local");
        Assert.Equal(0, projectInstall.ExitCode);
        var projectSettingsPath = Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json");
        using (var projectSettings = JsonDocument.Parse(await File.ReadAllTextAsync(projectSettingsPath)))
            Assert.Equal(Path.GetRelativePath(Path.Combine(fixture.ProjectDirectory, ".pi"), projectLocal),
                projectSettings.RootElement.GetProperty("packages")[0].GetString());

        var projectList = await RunAsync(fixture, "list", "--approve");
        Assert.Equal(0, projectList.ExitCode);
        Assert.Contains("Project packages:", projectList.Output);
        Assert.Contains(projectLocal, projectList.Output);

        var removedUser = await RunAsync(fixture, "remove", "./user-local");
        Assert.Equal(0, removedUser.ExitCode);
        var removedProject = await RunAsync(fixture, "uninstall", "--local", "--approve", "./project-local");
        Assert.Equal(0, removedProject.ExitCode);
        Assert.True(Directory.Exists(userLocal));
        Assert.True(Directory.Exists(projectLocal));
        using (var userSettings = JsonDocument.Parse(await File.ReadAllTextAsync(userSettingsPath)))
            Assert.Empty(userSettings.RootElement.GetProperty("packages").EnumerateArray());
        using (var projectSettings = JsonDocument.Parse(await File.ReadAllTextAsync(projectSettingsPath)))
            Assert.Empty(projectSettings.RootElement.GetProperty("packages").EnumerateArray());

        var emptyList = await RunAsync(fixture, "list");
        Assert.Equal(0, emptyList.ExitCode);
        Assert.Contains("No packages installed.", emptyList.Output);
    }

    [Fact]
    public async Task UserInstallDoesNotExecuteAnUntrustedProjectNpmCommand()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var packageDirectory = fixture.CreatePackageDirectory("package-cli-trust-control");
        var tarball = await fixture.PackAsync(packageDirectory,
            "{\"name\":\"package-cli-trust-control\",\"version\":\"1.0.0\"}");
        var marker = Path.Combine(fixture.Root, "project-npm-was-run");
        var shim = Path.Combine(fixture.Root, "project-npm.sh");
        await File.WriteAllTextAsync(shim, $"#!/bin/sh\nprintf called >> '{marker}'\nexit 91\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"), "{\"defaultProjectTrust\":\"never\"}");
        Directory.CreateDirectory(Path.Combine(fixture.ProjectDirectory, ".pi"));
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json"),
            JsonSerializer.Serialize(new { npmCommand = new[] { shim } }));

        var result = await RunAsync(fixture, "install", "npm:package-cli-trust-control@file:" + tarball);

        Assert.Equal(0, result.ExitCode);
        Assert.True(Directory.Exists(Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "package-cli-trust-control")));
        Assert.False(File.Exists(marker));
        using var projectSettings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json")));
        Assert.Equal(shim, projectSettings.RootElement.GetProperty("npmCommand")[0].GetString());
    }

    [Fact]
    public async Task PackageHelpAndUpdateTargetsFailBeforeSettingsOrAcquisition()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();

        var help = await RunAsync(fixture, "install", "--unknown", "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("pisharp install <source|local.nupkg>", help.Output);
        Assert.Contains("Examples:", help.Output);
        Assert.Contains("--no-approve", help.Output);

        var invalidList = await RunAsync(fixture, "list", "first", "second");
        Assert.Equal(1, invalidList.ExitCode);
        Assert.Contains("Unexpected argument second", invalidList.Error);

        var selfUpdate = await RunAsync(fixture, "update");
        Assert.Equal(1, selfUpdate.ExitCode);
        Assert.Contains("Self-update is unavailable", selfUpdate.Error);
        Assert.False(File.Exists(Path.Combine(fixture.AgentDirectory, "settings.json")));

        var packageUpdate = await RunAsync(fixture, "update", "--extensions", "--force");
        Assert.Equal(0, packageUpdate.ExitCode);
        Assert.Contains("Updated packages", packageUpdate.Output);
        var conflictingUpdate = await RunAsync(fixture, "update", "npm:sample", "--extensions");
        Assert.Equal(1, conflictingUpdate.ExitCode);
        Assert.Contains("cannot be combined", conflictingUpdate.Error);
        var modelConflict = await RunAsync(fixture, "update", "--models", "npm:sample");
        Assert.Equal(1, modelConflict.ExitCode);
        Assert.Contains("--models", modelConflict.Error);
    }

    [Fact]
    public async Task TrustedProjectNpmCommandOverridesUserCommandForLocalInstall()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var packageDirectory = fixture.CreatePackageDirectory("trusted-project-manager");
        var tarball = await fixture.PackAsync(packageDirectory,
            "{\"name\":\"trusted-project-manager\",\"version\":\"1.0.0\"}");
        var userLog = Path.Combine(fixture.Root, "user-manager-argv.log");
        var projectLog = Path.Combine(fixture.Root, "project-manager-argv.log");
        var userCommand = CreateNpmLoggingShim(fixture, "user-npm", "PISHARP_USER_NPM_LOG");
        var projectCommand = CreateNpmLoggingShim(fixture, "project-npm", "PISHARP_PROJECT_NPM_LOG");
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"),
            JsonSerializer.Serialize(new { npmCommand = new[] { userCommand } }));
        var projectSettings = Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(projectSettings)!);
        await File.WriteAllTextAsync(projectSettings,
            JsonSerializer.Serialize(new { npmCommand = new[] { projectCommand } }));

        var source = "npm:trusted-project-manager@file:" + tarball;
        var installed = await RunAsync(fixture, new Dictionary<string, string?>
        {
            ["PISHARP_USER_NPM_LOG"] = userLog,
            ["PISHARP_PROJECT_NPM_LOG"] = projectLog
        }, "install", "--local", "--approve", source);

        Assert.True(installed.ExitCode == 0, installed.Error + "\n" + installed.Output);
        Assert.False(File.Exists(userLog));
        var projectArgv = File.ReadAllText(projectLog).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["install", "trusted-project-manager@file:" + tarball, "--prefix",
            Path.Combine(fixture.ProjectDirectory, ".pi", "npm"), "--legacy-peer-deps"], projectArgv);
        Assert.True(Directory.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "npm", "node_modules",
            "trusted-project-manager")));
    }

    [Fact]
    public async Task PackageInstallForwardsSuccessfulNpmDiagnosticsToStderr()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var packageDirectory = fixture.CreatePackageDirectory("npm-diagnostic-forwarding");
        var tarball = await fixture.PackAsync(packageDirectory,
            "{\"name\":\"npm-diagnostic-forwarding\",\"version\":\"1.0.0\"}");
        var shim = Path.Combine(fixture.Root, "npm-with-diagnostic");
        await File.WriteAllTextAsync(shim,
            "#!/bin/sh\nprintf 'npm install output\\n'\nprintf 'npm install diagnostic\\n' >&2\nexec npm \"$@\"\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"),
            JsonSerializer.Serialize(new { npmCommand = new[] { shim } }));

        var result = await RunAsync(fixture, "install", "npm:npm-diagnostic-forwarding@file:" + tarball);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("npm install output", result.Output);
        Assert.Contains("npm install diagnostic", result.Error);
    }

    [Fact]
    public async Task OfflinePackageUpdateDoesNotInvokeConfiguredPackageManager()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var managerLog = Path.Combine(fixture.Root, "offline-update-manager.log");
        var shim = Path.Combine(fixture.Root, "offline-npm");
        await File.WriteAllTextAsync(shim,
            "#!/bin/sh\nprintf '%s\\0' \"$@\" >> \"$PISHARP_OFFLINE_NPM_LOG\"\nexit 91\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"),
            JsonSerializer.Serialize(new { npmCommand = new[] { shim }, packages = new[] { "npm:offline-update-control" } }));

        var result = await RunAsync(fixture, new Dictionary<string, string?>
        {
            ["PI_OFFLINE"] = "true",
            ["PISHARP_OFFLINE_NPM_LOG"] = managerLog
        }, "update", "--extensions");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Updated packages", result.Output);
        Assert.False(File.Exists(managerLog));
    }

    [Fact]
    public async Task ProjectLocalUpdateDoesNotMatchAUserRelativePathAlias()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var userPath = Path.Combine(fixture.AgentDirectory, "project-relative-package");
        var projectPath = Path.Combine(fixture.ProjectDirectory, ".pi", "project-relative-package");
        Directory.CreateDirectory(userPath);
        Directory.CreateDirectory(projectPath);
        var projectSettingsPath = Path.Combine(fixture.ProjectDirectory, ".pi", "settings.json");
        await File.WriteAllTextAsync(projectSettingsPath,
            JsonSerializer.Serialize(new { packages = new[] { "project-relative-package" } }));

        var result = await RunAsync(fixture, new Dictionary<string, string?> { ["PI_OFFLINE"] = "true" },
            "update", "--approve", userPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No configured package matches", result.Error);
    }

    [Fact]
    public async Task MalformedSettingsReturnsControlledErrorWithoutAcquiringOrChangingSettings()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var packageDirectory = fixture.CreatePackageDirectory("malformed-settings-control");
        var tarball = await fixture.PackAsync(packageDirectory,
            "{\"name\":\"malformed-settings-control\",\"version\":\"1.0.0\"}");
        var settingsPath = Path.Combine(fixture.AgentDirectory, "settings.json");
        const string malformed = "{\"packages\":[";
        await File.WriteAllTextAsync(settingsPath, malformed);

        var result = await RunAsync(fixture, "install", "npm:malformed-settings-control@file:" + tarball);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Error:", result.Error);
        Assert.Equal(malformed, await File.ReadAllTextAsync(settingsPath));
        Assert.False(Directory.Exists(Path.Combine(fixture.AgentDirectory, "npm")));
    }

    [Fact]
    public async Task NpmInstallAndRemovePreservePackageFiltersAndUseTheInstalledSkill()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var packageDirectory = fixture.CreatePackageDirectory("cli-package-persistence");
        var tarball = await fixture.PackAsync(packageDirectory,
            "{\"name\":\"cli-package-persistence\",\"version\":\"1.0.0\",\"pi\":{\"skills\":[\"skills\"]}}",
            new Dictionary<string, string>
            {
                ["skills/guide/SKILL.md"] = "---\nname: installed-cli-guide\ndescription: Installed by package command\n---\nUse this installed guide."
            });
        var source = "npm:cli-package-persistence@file:" + tarball;
        var settingsPath = Path.Combine(fixture.AgentDirectory, "settings.json");
        var prior = "npm:cli-package-persistence@^0.9.0";
        var duplicate = "npm:cli-package-persistence@^0.8.0";
        const string unrelated = "npm:unrelated-package@1.0.0";
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new
        {
            quietStartup = true,
            defaultThinkingLevel = "high",
            packages = new object[]
            {
                new { source = prior, skills = new[] { "skills/**/*.md" }, custom = new { field = "retained" } },
                new { source = duplicate, prompts = new[] { "prompts/**/*.md" }, custom = "later duplicate retained" },
                unrelated
            }
        }));

        var installed = await RunAsync(fixture, "install", source);
        Assert.True(installed.ExitCode == 0, installed.Error + "\n" + installed.Output);
        Assert.Contains($"Installing {source}...", installed.Output);

        using (var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath)))
        {
            var root = settings.RootElement;
            Assert.True(root.GetProperty("quietStartup").GetBoolean());
            Assert.Equal("high", root.GetProperty("defaultThinkingLevel").GetString());
            var packages = root.GetProperty("packages");
            Assert.Equal(3, packages.GetArrayLength());
            var first = packages[0];
            Assert.Equal(source, first.GetProperty("source").GetString());
            Assert.Equal("retained", first.GetProperty("custom").GetProperty("field").GetString());
            Assert.Equal("skills/**/*.md", first.GetProperty("skills")[0].GetString());
            Assert.Equal(duplicate, packages[1].GetProperty("source").GetString());
            Assert.Equal(unrelated, packages[2].GetString());
        }

        var packageRoot = Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "cli-package-persistence");
        var catalog = await ResourceCatalog.LoadAsync(fixture.ProjectDirectory, fixture.AgentDirectory, trusted: false,
            userPackages: [new(source, Skills: ["skills/**/*.md"], HasFilter: true)],
            managedPackageRoots: [new(source, "user", packageRoot)]);
        var skill = Assert.Single(catalog.Skills, item => item.Name == "installed-cli-guide");
        Assert.Equal(source, skill.SourceInfo.Source);
        Assert.Equal(packageRoot, skill.SourceInfo.BaseDir);

        var removed = await RunAsync(fixture, "remove", source);
        Assert.True(removed.ExitCode == 0, removed.Error + "\n" + removed.Output);
        Assert.Contains($"Removing {source}...", removed.Output);
        Assert.False(Directory.Exists(Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "cli-package-persistence")));
        using var afterRemoval = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(1, afterRemoval.RootElement.GetProperty("packages").GetArrayLength());
        Assert.Equal(unrelated, afterRemoval.RootElement.GetProperty("packages")[0].GetString());
    }

    [Fact]
    public async Task PackageUpdateUpgradesANpmRangeFromALoopbackRegistry()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var oldPackage = fixture.CreatePackageDirectory("range-update-v1");
        var oldTarball = await fixture.PackAsync(oldPackage,
            "{\"name\":\"pisharp-range-fixture\",\"version\":\"1.0.0\"}");
        var newPackage = fixture.CreatePackageDirectory("range-update-v2");
        var newTarball = await fixture.PackAsync(newPackage,
            "{\"name\":\"pisharp-range-fixture\",\"version\":\"1.1.0\"}");
        await using var registry = new LoopbackNpmRegistry("pisharp-range-fixture");
        registry.SetVersions(("1.0.0", oldTarball));
        var env = new Dictionary<string, string?>
        {
            ["npm_config_registry"] = registry.Url,
            ["npm_config_audit"] = "false",
            ["npm_config_fund"] = "false",
            ["npm_config_fetch_retries"] = "0",
            ["npm_config_offline"] = "false"
        };

        var source = "npm:pisharp-range-fixture@^1.0.0";
        var installed = await RunAsync(fixture, env, "install", source);
        Assert.True(installed.ExitCode == 0, installed.Error + "\n" + installed.Output);
        var packagePath = Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "pisharp-range-fixture", "package.json");
        Assert.Equal("1.0.0", ReadVersion(packagePath));

        registry.SetVersions(("1.0.0", oldTarball), ("1.1.0", newTarball));
        var updated = await RunAsync(fixture, env, "update", "--extensions");
        Assert.True(updated.ExitCode == 0, updated.Error + "\n" + updated.Output);
        Assert.Equal("1.1.0", ReadVersion(packagePath));
    }

    [Fact]
    public async Task PackageUpdateUpgradesUnversionedLatestFromALoopbackRegistry()
    {
        await using var fixture = new ManagedNpmFixture();
        fixture.Create();
        var oldPackage = fixture.CreatePackageDirectory("latest-update-v1");
        var oldTarball = await fixture.PackAsync(oldPackage,
            "{\"name\":\"pisharp-latest-fixture\",\"version\":\"1.0.0\"}");
        var newPackage = fixture.CreatePackageDirectory("latest-update-v2");
        var newTarball = await fixture.PackAsync(newPackage,
            "{\"name\":\"pisharp-latest-fixture\",\"version\":\"1.1.0\"}");
        await using var registry = new LoopbackNpmRegistry("pisharp-latest-fixture");
        registry.SetVersions(("1.0.0", oldTarball));
        var env = new Dictionary<string, string?>
        {
            ["npm_config_registry"] = registry.Url,
            ["npm_config_audit"] = "false",
            ["npm_config_fund"] = "false",
            ["npm_config_fetch_retries"] = "0",
            ["npm_config_offline"] = "false"
        };

        var source = "npm:pisharp-latest-fixture";
        var installed = await RunAsync(fixture, env, "install", source);
        Assert.True(installed.ExitCode == 0, installed.Error + "\n" + installed.Output);
        var packagePath = Path.Combine(fixture.AgentDirectory, "npm", "node_modules", "pisharp-latest-fixture", "package.json");
        Assert.Equal("1.0.0", ReadVersion(packagePath));

        registry.SetVersions(("1.0.0", oldTarball), ("1.1.0", newTarball));
        var updated = await RunAsync(fixture, env, "update", "--extensions");
        Assert.True(updated.ExitCode == 0, updated.Error + "\n" + updated.Output);
        Assert.Equal("1.1.0", ReadVersion(packagePath));
    }

    [Fact]
    public async Task GitPackageCommandsInstallUpdateAndRemoveOnlyTheSelectedScope()
    {
        await using var fixture = new ManagedGitFixture();
        await fixture.CreateAsync();
        await fixture.SetRemoteDefaultBranchAsync(fixture.Version1);
        var userSource = fixture.UnpinnedSource;
        var installed = await RunGitCommandAsync(fixture, "install", userSource);
        Assert.True(installed.ExitCode == 0, installed.Error + "\n" + installed.Output);
        Assert.Contains($"Installing {userSource}...", installed.Output);
        Assert.Equal(fixture.Version1, await ReadGitHeadAsync(fixture.UserCacheRoot));

        await fixture.SetRemoteDefaultBranchAsync(fixture.Version2);
        var updated = await RunGitCommandAsync(fixture, "update", "--extensions");
        Assert.Equal(0, updated.ExitCode);
        Assert.Equal(fixture.Version2, await ReadGitHeadAsync(fixture.UserCacheRoot));

        var projectSource = fixture.Source("v1");
        var projectInstalled = await RunGitCommandAsync(fixture, "install", "--local", "--approve", projectSource);
        Assert.Equal(0, projectInstalled.ExitCode);
        var projectCache = Path.Combine(fixture.ProjectDirectory, ".pi", "git", "127.0.0.1", "fixture", "repo");
        Assert.True(Directory.Exists(projectCache));

        var listed = await RunGitCommandAsync(fixture, "list", "--approve");
        Assert.Equal(0, listed.ExitCode);
        Assert.Contains("User packages:", listed.Output);
        Assert.Contains("Project packages:", listed.Output);
        Assert.Contains(fixture.UserCacheRoot, listed.Output);
        Assert.Contains(projectCache, listed.Output);

        var userRemoved = await RunGitCommandAsync(fixture, "remove", userSource);
        Assert.Equal(0, userRemoved.ExitCode);
        Assert.False(Directory.Exists(fixture.UserCacheRoot));
        Assert.True(Directory.Exists(projectCache));
        var projectRemoved = await RunGitCommandAsync(fixture, "remove", "--local", "--approve", projectSource);
        Assert.Equal(0, projectRemoved.ExitCode);
        Assert.False(Directory.Exists(projectCache));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ManagedNpmFixture fixture, IReadOnlyDictionary<string, string?>? extraEnvironment, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions");
        start.Environment["npm_config_cache"] = fixture.NpmCacheDirectory;
        start.Environment["npm_config_offline"] = "true";
        start.Environment.Remove("PISHARP_SETTINGS_PATH");
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string?>())
            start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static Task<(int ExitCode, string Output, string Error)> RunAsync(ManagedNpmFixture fixture,
        params string[] arguments) => RunAsync(fixture, extraEnvironment: null, arguments: arguments);

    private static string ReadVersion(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("version").GetString()!;
    }

    private static string CreateNpmLoggingShim(ManagedNpmFixture fixture, string name, string environmentVariable)
    {
        var shim = Path.Combine(fixture.Root, name);
        File.WriteAllText(shim, $"#!/bin/sh\nprintf '%s\\0' \"$@\" >> \"${environmentVariable}\"\nexec npm \"$@\"\n");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shim;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunGitCommandAsync(
        ManagedGitFixture fixture, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.ProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PISHARP_AGENT_DIR"] = fixture.AgentDirectory;
        start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(fixture.Root, "sessions");
        foreach (var (key, value) in fixture.CreateProcessEnvironment()) start.Environment[key] = value;
        start.Environment.Remove("PISHARP_SETTINGS_PATH");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static async Task<string> ReadGitHeadAsync(string path)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("HEAD");
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(error);
        return output.Trim();
    }

    private static async Task CreateNativeNupkgAsync(string path, string id, string version, bool includeAssembly,
        int apiVersion = 1)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("pisharp-extension.json");
        await using (var writer = new StreamWriter(manifest.Open()))
        {
            await writer.WriteAsync(JsonSerializer.Serialize(new
            {
                id,
                version,
                apiVersion,
                extensions = new[] { new { assembly = "lib/net10.0/Example.NativeFixture.dll", type = "Example.NativeFixture.Entry" } }
            }));
        }
        if (includeAssembly)
        {
            var assembly = archive.CreateEntry("lib/net10.0/Example.NativeFixture.dll");
            await using var stream = assembly.Open();
            await stream.WriteAsync(new byte[] { 0x4d, 0x5a, 0x00, 0x01 });
        }
    }

    private sealed class LoopbackNpmRegistry : IAsyncDisposable
    {
        private readonly string _packageName;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveTask;
        private readonly object _gate = new();
        private (string Version, string Tarball)[] _versions = [];

        public LoopbackNpmRegistry(string packageName)
        {
            _packageName = packageName;
            (_listener, var port) = TestLoopbackListener.Start();
            Url = $"http://127.0.0.1:{port}/";
            _serveTask = ServeAsync();
        }

        public string Url { get; }

        public void SetVersions(params (string Version, string Tarball)[] versions)
        {
            lock (_gate) _versions = versions.ToArray();
        }

        private async Task ServeAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token); }
                catch (OperationCanceledException) { return; }
                catch (HttpListenerException) when (_shutdown.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { return; }
                _ = RespondAsync(context);
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath.Trim('/') ?? "";
                if (path == _packageName)
                {
                    (string Version, string Tarball)[] versions;
                    lock (_gate) versions = _versions.ToArray();
                    var packument = new Dictionary<string, object?>
                    {
                        ["name"] = _packageName,
                        ["versions"] = versions.ToDictionary(entry => entry.Version, entry => new
                        {
                            name = _packageName,
                            version = entry.Version,
                            dist = new { tarball = Url + _packageName + "/-/" + _packageName + "-" + entry.Version + ".tgz" }
                        }),
                        ["dist-tags"] = new { latest = versions.LastOrDefault().Version ?? "1.0.0" }
                    };
                    await WriteAsync(context, JsonSerializer.SerializeToUtf8Bytes(packument), "application/json");
                    return;
                }
                var filename = Path.GetFileName(path);
                (string Version, string Tarball)[] known;
                lock (_gate) known = _versions.ToArray();
                var match = known.FirstOrDefault(entry => filename == $"{_packageName}-{entry.Version}.tgz");
                if (match.Tarball is not null && File.Exists(match.Tarball))
                {
                    await WriteAsync(context, await File.ReadAllBytesAsync(match.Tarball), "application/octet-stream");
                    return;
                }
                context.Response.StatusCode = 404;
                await WriteAsync(context, "Not found"u8.ToArray(), "text/plain");
            }
            catch (HttpListenerException) { }
            finally { context.Response.Close(); }
        }

        private static async Task WriteAsync(HttpListenerContext context, byte[] body, string contentType)
        {
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Close();
            try { await _serveTask; }
            catch (HttpListenerException) { }
            _shutdown.Dispose();
        }
    }
}
