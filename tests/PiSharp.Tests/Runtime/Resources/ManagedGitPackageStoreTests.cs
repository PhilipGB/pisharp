using System.Diagnostics;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Tests.TestInfrastructure;

namespace PiSharp.Tests;

[Collection("Managed npm subprocesses")]
public sealed class ManagedGitPackageStoreTests
{
    [Theory]
    [InlineData("git:github.com/acme/tool.git@v1", "git:github.com/acme/tool")]
    [InlineData("git:git@github.com:acme/tool.git@v1", "git:github.com/acme/tool")]
    [InlineData("git:https://github.com/acme/tool.git@v1", "git:github.com/acme/tool")]
    [InlineData("ssh://git@github.com/acme/tool.git#v1", "git:github.com/acme/tool")]
    public void SupportedGitSyntaxUsesVersionIndependentRepositoryIdentity(string source, string expected)
    {
        Assert.True(ManagedResourcePackageStore.TryGetGitIdentity(source, out var identity));
        Assert.Equal(expected, identity);
    }

    [Theory]
    [InlineData("git:git@evil.example:../../victim/repo")]
    [InlineData("https://evil.example/..%2F..%2Fvictim/repo")]
    [InlineData("https://evil.example/repo%")]
    [InlineData("git:git@evil.example:/absolute/repo")]
    [InlineData("git:git@evil.example:user\\repo/name")]
    [InlineData("git:git@evil.example:user/repo\0name")]
    [InlineData("git@github.com:acme/tool")]
    [InlineData("github.com/acme/tool")]
    public void UnsafeOrUnprefixedShorthandGitSourcesAreNotManaged(string source)
    {
        Assert.False(ManagedResourcePackageStore.TryGetGitIdentity(source, out _));
    }

    [Fact]
    public async Task GitDependencyInstallUsesConfiguredManagerPoliciesAndUnknownFallback()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var fixture = new ManagedGitFixture();
        await fixture.CreateAsync();
        await fixture.SetRemoteDefaultBranchAsync(fixture.Version1);
        var logPath = Path.Combine(fixture.Root, "git-manager-argv.log");
        var shim = Path.Combine(fixture.Root, "npm-manager-wrapper");
        await File.WriteAllTextAsync(shim,
            "#!/bin/sh\nprintf 'CALL\\0' >> \"$PISHARP_TEST_NPM_ARGV\"\nprintf '%s\\0' \"$@\" >> \"$PISHARP_TEST_NPM_ARGV\"\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = fixture.CreateProcessEnvironment();
        environment["PISHARP_TEST_NPM_ARGV"] = logPath;
        var store = new ManagedResourcePackageStore(fixture.AgentDirectory, environment);
        var managers = new[] { "npm", "pnpm", "bun", "otherpm" };

        foreach (var manager in managers)
        {
            store.ConfigureNpmCommand([shim, "exec", "node@20", "--", manager]);
            await store.InstallSelectedAsync(fixture.ProjectDirectory, fixture.Source("v1"), "user");
        }

        var tokens = File.ReadAllText(logPath).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var expected = new List<string>();
        foreach (var manager in managers)
        {
            expected.Add("CALL");
            expected.AddRange(["exec", "node@20", "--", manager, "install"]);
            expected.AddRange(manager switch
            {
                "bun" => ["--omit=dev", "--omit=peer"],
                "pnpm" => ["--prod", "--config.auto-install-peers=false", "--config.strict-peer-dependencies=false",
                    "--config.strict-dep-builds=false"],
                "npm" => ["--omit=dev", "--legacy-peer-deps"],
                _ => []
            });
        }
        Assert.Equal(expected, tokens);
    }

    [Fact]
    public async Task ManagedGitLoadsResourcesAndNativeExtensionWithoutStartupFetchAndUpdatesConfiguredRef()
    {
        await using var fixture = new ManagedGitFixture();
        await fixture.CreateAsync();
        var previousEnvironment = SetEnvironment(fixture.CreateProcessEnvironment());
        try
        {
            var sourceV1 = fixture.Source("v1");
            await WriteUserSettingsAsync(fixture, sourceV1);
            var arguments = CliArguments.Parse([]);
            await AssertStartupResourcesAsync(fixture, arguments, "git-guide-v1", sourceV1);
            Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.PackageInstallLog));
            Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.DependencyInstallLog));

            var deltaSource = fixture.Source("v2");
            var deltaRoots = await new ManagedResourcePackageStore(fixture.AgentDirectory)
                .ResolveAsync(fixture.ProjectDirectory, trusted: true,
                    userPackages: [new(sourceV1)],
                    projectPackages: [new(deltaSource, Skills: ["skills/**/*.md"], Autoload: false, HasFilter: true)],
                    offline: true);
            var deltaRoot = Assert.Single(deltaRoots, root => root.Scope == "project");
            Assert.Equal(deltaSource, deltaRoot.Source);
            Assert.Equal(fixture.UserCacheRoot, deltaRoot.Path);

            await fixture.StopDaemonAsync();
            await AssertStartupResourcesAsync(fixture, arguments, "git-guide-v1", sourceV1);
            Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.PackageInstallLog));
            Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.DependencyInstallLog));

            await fixture.StartDaemonAsync();
            var store = new ManagedResourcePackageStore(fixture.AgentDirectory, fixture.CreateProcessEnvironment());
            var unchanged = await store.UpdateAsync(fixture.ProjectDirectory, trusted: false,
                userPackages: [new(sourceV1)], source: sourceV1);
            Assert.Equal(fixture.Version1, await ReadHeadAsync(fixture.UserCacheRoot));
            Assert.Single(unchanged);
            Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.PackageInstallLog));
            Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.DependencyInstallLog));

            var sourceV2 = fixture.Source("v2");
            var updated = await store.UpdateAsync(fixture.ProjectDirectory, trusted: false,
                userPackages: [new(sourceV2)], source: sourceV2);
            Assert.Equal(fixture.Version2, await ReadHeadAsync(fixture.UserCacheRoot));
            Assert.Single(updated);
            Assert.Equal(["run", "run"], await File.ReadAllLinesAsync(fixture.PackageInstallLog));
            Assert.Equal(["run", "run"], await File.ReadAllLinesAsync(fixture.DependencyInstallLog));
            Assert.True(Directory.Exists(Path.Combine(fixture.UserCacheRoot, "node_modules", "managed-git-fixture-dependency")));
            Assert.False(Directory.Exists(Path.Combine(fixture.UserCacheRoot, "node_modules", "missing-host-peer")));

            await WriteUserSettingsAsync(fixture, sourceV2);
            await AssertStartupResourcesAsync(fixture, arguments, "git-guide-v2", sourceV2);
            Assert.Equal(["run", "run"], await File.ReadAllLinesAsync(fixture.PackageInstallLog));
            Assert.Equal(["run", "run"], await File.ReadAllLinesAsync(fixture.DependencyInstallLog));
        }
        finally { RestoreEnvironment(previousEnvironment); }
    }

    [Fact]
    public async Task OfflineMissingDeltaAndFailedOrCancelledUpdatesKeepTheLastUsableCheckout()
    {
        await using var fixture = new ManagedGitFixture();
        await fixture.CreateAsync();
        var environment = fixture.CreateProcessEnvironment();
        var store = new ManagedResourcePackageStore(fixture.AgentDirectory, environment);
        var sourceV1 = fixture.Source("v1");
        var sourceV2 = fixture.Source("v2");
        var installed = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false, userPackages: [new(sourceV1)]);
        Assert.Single(installed);
        Assert.Equal(fixture.Version1, await ReadHeadAsync(fixture.UserCacheRoot));

        var emptyAgent = Path.Combine(fixture.Root, "empty-agent");
        Directory.CreateDirectory(emptyAgent);
        var unresolvedDelta = await new ManagedResourcePackageStore(emptyAgent, environment).ResolveAsync(
            fixture.ProjectDirectory, trusted: true,
            userPackages: [new(sourceV1)],
            projectPackages: [new(sourceV2, Autoload: false, Skills: ["skills/**/*.md"], HasFilter: true)],
            offline: true);
        Assert.Empty(unresolvedDelta);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectDirectory, ".pi", "git")));

        await fixture.StopDaemonAsync();
        await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new(sourceV2)], source: sourceV2));
        Assert.Equal(fixture.Version1, await ReadHeadAsync(fixture.UserCacheRoot));
        Assert.True(File.Exists(Path.Combine(fixture.UserCacheRoot, "skills", "git-guide-v1", "SKILL.md")));

        await fixture.StartDaemonAsync();
        environment["PISHARP_TEST_GIT_FAIL_DEPENDENCY"] = "1";
        await Assert.ThrowsAsync<IOException>(() => store.UpdateAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new(sourceV2)], source: sourceV2));
        Assert.Equal(fixture.Version1, await ReadHeadAsync(fixture.UserCacheRoot));
        Assert.True(File.Exists(Path.Combine(fixture.UserCacheRoot, "skills", "git-guide-v1", "SKILL.md")));
        Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.DependencyInstallLog));
        environment.Remove("PISHARP_TEST_GIT_FAIL_DEPENDENCY");

        if (!OperatingSystem.IsLinux()) return;
        var wrapper = Path.Combine(fixture.Root, "fake-bin");
        Directory.CreateDirectory(wrapper);
        var realGit = FindExecutable("git");
        var fakeGit = Path.Combine(wrapper, "git");
        await File.WriteAllTextAsync(fakeGit,
            $"#!/bin/sh\nif [ \"$1\" = \"clone\" ] && [ \"$PISHARP_TEST_BLOCK_GIT_CLONE\" = \"1\" ]; then\n" +
            "  echo $$ > \"$PISHARP_TEST_GIT_PID_FILE\"\n  exec /usr/bin/sleep 60\nfi\n" +
            $"exec {realGit} \"$@\"\n");
        File.SetUnixFileMode(fakeGit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var testPath = wrapper + Path.PathSeparator + previousPath;
        Environment.SetEnvironmentVariable("PATH", testPath);
        environment["PATH"] = testPath;
        var pidFile = Path.Combine(fixture.Root, "blocked-git.pid");
        environment["PISHARP_TEST_BLOCK_GIT_CLONE"] = "1";
        environment["PISHARP_TEST_GIT_PID_FILE"] = pidFile;
        try
        {
            using var cancellation = new CancellationTokenSource();
            var update = store.UpdateAsync(fixture.ProjectDirectory, trusted: false,
                userPackages: [new(sourceV2)], source: sourceV2, cancellationToken: cancellation.Token);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!File.Exists(pidFile) && !update.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (!File.Exists(pidFile) && update.IsCompleted) await update;
            Assert.True(File.Exists(pidFile), "The staged git clone was not started.");
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            Assert.True(await WaitForProcessExitAsync(pid), "The Git child process remained alive after cancellation.");
            Assert.Equal(fixture.Version1, await ReadHeadAsync(fixture.UserCacheRoot));
            Assert.True(File.Exists(Path.Combine(fixture.UserCacheRoot, "skills", "git-guide-v1", "SKILL.md")));
        }
        finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
    }

    [Fact]
    public async Task CachedUnpinnedGitSourceRefreshesOnlyOnExplicitUpdate()
    {
        await using var fixture = new ManagedGitFixture();
        await fixture.CreateAsync();
        await fixture.SetRemoteDefaultBranchAsync(fixture.Version1);
        var source = fixture.UnpinnedSource;
        var store = new ManagedResourcePackageStore(fixture.AgentDirectory, fixture.CreateProcessEnvironment());

        var installed = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false, userPackages: [new(source)]);
        Assert.Single(installed);
        Assert.Equal(fixture.Version1, await ReadHeadAsync(fixture.UserCacheRoot));

        await fixture.SetRemoteDefaultBranchAsync(fixture.Version2);
        var cached = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false, userPackages: [new(source)]);
        Assert.Single(cached);
        Assert.Equal(fixture.Version1, await ReadHeadAsync(fixture.UserCacheRoot));
        Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.PackageInstallLog));
        Assert.Equal(["run"], await File.ReadAllLinesAsync(fixture.DependencyInstallLog));

        var updated = await store.UpdateAsync(fixture.ProjectDirectory, trusted: false,
            userPackages: [new(source)], source: source);
        Assert.Single(updated);
        Assert.Equal(fixture.Version2, await ReadHeadAsync(fixture.UserCacheRoot));
        Assert.Equal(["run", "run"], await File.ReadAllLinesAsync(fixture.PackageInstallLog));
        Assert.Equal(["run", "run"], await File.ReadAllLinesAsync(fixture.DependencyInstallLog));
    }

    [Fact]
    public async Task UserAndProjectGitCacheRootsAreIgnoredByGit()
    {
        await using var fixture = new ManagedGitFixture();
        await fixture.CreateAsync();
        var source = fixture.Source("v1");
        var store = new ManagedResourcePackageStore(fixture.AgentDirectory, fixture.CreateProcessEnvironment());
        var userRoots = await store.ResolveAsync(fixture.ProjectDirectory, trusted: false, userPackages: [new(source)]);
        Assert.Single(userRoots);
        Assert.Equal("*\n!.gitignore\n", await File.ReadAllTextAsync(Path.Combine(fixture.AgentDirectory, "git", ".gitignore")));

        var projectRoots = await store.ResolveAsync(fixture.ProjectDirectory, trusted: true,
            projectPackages: [new(source)]);
        var projectRoot = Assert.Single(projectRoots);
        Assert.Equal("project", projectRoot.Scope);
        Assert.Equal("*\n!.gitignore\n", await File.ReadAllTextAsync(Path.Combine(fixture.ProjectDirectory, ".pi", "git", ".gitignore")));
    }

    [Fact]
    public async Task ExplicitUpdatesSkipExactNpmVersionsAndOnlyInstallWhenConfiguredTargetIsNewer()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-npm-update-policy-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        var projectDirectory = Path.Combine(root, "project");
        var npmPackage = Path.Combine(agentDirectory, "npm", "node_modules", "range-package");
        var npmLog = Path.Combine(root, "npm.log");
        var fakeBin = Path.Combine(root, "bin");
        var fakeNpm = Path.Combine(fakeBin, "npm");
        Directory.CreateDirectory(npmPackage);
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(fakeBin);
        await File.WriteAllTextAsync(Path.Combine(npmPackage, "package.json"),
            "{\"name\":\"range-package\",\"version\":\"1.0.0\"}");
        await File.WriteAllTextAsync(fakeNpm,
            "#!/bin/sh\n" +
            "if [ \"$1\" = \"outdated\" ]; then\n" +
            "  printf '{\"range-package\":{\"current\":\"%s\",\"wanted\":\"%s\"}}\\n' \"$PISHARP_TEST_NPM_CURRENT\" \"$PISHARP_TEST_NPM_WANTED\"\n" +
            "  exit 1\nfi\n" +
            "if [ \"$1\" = \"install\" ]; then echo \"$2\" >> \"$PISHARP_TEST_NPM_UPDATE_LOG\"; exit 0; fi\n" +
            "if [ \"$1\" = \"ls\" ]; then printf '{\"dependencies\":{\"range-package\":{\"version\":\"%s\"}}}\\n' \"$PISHARP_TEST_NPM_CURRENT\"; exit 0; fi\n" +
            "exit 1\n");
        File.SetUnixFileMode(fakeNpm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var fakePath = fakeBin + Path.PathSeparator + previousPath;
        Environment.SetEnvironmentVariable("PATH", fakePath);
        var processEnvironment = new Dictionary<string, string?>
        {
            ["PATH"] = fakePath,
            ["npm_config_cache"] = Path.Combine(root, "npm-cache"),
            ["PISHARP_TEST_NPM_UPDATE_LOG"] = npmLog,
            ["PISHARP_TEST_NPM_CURRENT"] = "1.0.0",
            ["PISHARP_TEST_NPM_WANTED"] = "2.0.0"
        };
        try
        {
            var store = new ManagedResourcePackageStore(agentDirectory, processEnvironment);
            var unversioned = new ResourcePackageSource("npm:range-package");
            await store.UpdateAsync(projectDirectory, trusted: false, userPackages: [unversioned], source: unversioned.Source);
            Assert.Equal(["range-package@latest"], await File.ReadAllLinesAsync(npmLog));

            var exact = new ResourcePackageSource("npm:range-package@1.0.0");
            await store.UpdateAsync(projectDirectory, trusted: false, userPackages: [exact], source: exact.Source);
            Assert.Equal(["range-package@latest"], await File.ReadAllLinesAsync(npmLog));

            processEnvironment["PISHARP_TEST_NPM_CURRENT"] = "2.0.0";
            processEnvironment["PISHARP_TEST_NPM_WANTED"] = "1.9.0";
            var lowerRange = new ResourcePackageSource("npm:range-package@^1.0.0");
            await store.UpdateAsync(projectDirectory, trusted: false, userPackages: [lowerRange], source: lowerRange.Source);
            Assert.Equal(["range-package@latest"], await File.ReadAllLinesAsync(npmLog));

            processEnvironment["PISHARP_TEST_NPM_CURRENT"] = "1.0.0";
            processEnvironment["PISHARP_TEST_NPM_WANTED"] = "1.2.0-beta.2";
            var distTag = new ResourcePackageSource("npm:range-package@beta1");
            await store.UpdateAsync(projectDirectory, trusted: false, userPackages: [distTag], source: distTag.Source);
            Assert.Equal(["range-package@latest", "range-package@beta1"], await File.ReadAllLinesAsync(npmLog));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertStartupResourcesAsync(ManagedGitFixture fixture, CliArguments arguments,
        string skillName, string source)
    {
        using var configuration = await ProjectRuntimeConfiguration.LoadAsync(fixture.ProjectDirectory,
            fixture.AgentDirectory, arguments, new ProjectTrust(fixture.AgentDirectory), interactiveTrust: false,
            TextReader.Null, TextWriter.Null, trustedOverride: true);
        using var runtime = await ProjectRuntimeContext.LoadAsync(configuration, fixture.AgentDirectory, arguments,
            configuredSessionDirectory: null, resourceDiscoveryReason: ExtensionResourceDiscoveryReason.Startup);
        var skill = Assert.Single(runtime.Resources.Skills, item => item.Name == skillName);
        Assert.Equal(source, skill.SourceInfo.Source);
        Assert.Equal("user", skill.SourceInfo.Scope);
        Assert.Equal(fixture.UserCacheRoot, skill.SourceInfo.BaseDir);
        Assert.Contains(runtime.Resources.Prompts, item => item.Name == "review");
        Assert.Single(runtime.Extensions.LoadedExtensions);
        var themes = new TerminalThemeCatalog(fixture.AgentDirectory, fixture.ProjectDirectory,
            environment: _ => null, trueColorOverride: true,
            userPackages: configuration.BaseUserSettings.Packages,
            projectPackages: configuration.ProjectSettings?.Packages,
            managedPackageRoots: configuration.ManagedPackageRoots);
        Assert.Contains("managed-git-theme", themes.GetAvailableNames());
    }

    private static async Task WriteUserSettingsAsync(ManagedGitFixture fixture, string source) =>
        await File.WriteAllTextAsync(Path.Combine(fixture.AgentDirectory, "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new { packages = new[] { source } }));

    private static async Task<string> ReadHeadAsync(string path)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("rev-parse");
        process.StartInfo.ArgumentList.Add("HEAD");
        if (!process.Start()) throw new IOException("Could not start git rev-parse.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await stderr);
        return (await stdout).Trim();
    }

    private static Dictionary<string, string?> SetEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            previous[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        return previous;
    }

    private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
    }

    private static string FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not find {name} on PATH.");
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
