using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Tui;
using PiSharp.ResourceDiscoveryExtension;

namespace PiSharp.Tests.TestInfrastructure;

internal sealed class ManagedGitFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-managed-git-" + Guid.NewGuid().ToString("N"));
    public string AgentDirectory => Path.Combine(Root, "agent");
    public string ProjectDirectory => Path.Combine(Root, "project");
    public string NpmCacheDirectory => Path.Combine(Root, "npm-cache");
    public string ExportDirectory => Path.Combine(Root, "git-export");
    public string RepositoryDirectory => Path.Combine(ExportDirectory, "fixture", "repo.git");
    public string PackageInstallLog => Path.Combine(Root, "package-install.log");
    public string DependencyInstallLog => Path.Combine(Root, "dependency-install.log");
    public string ExtensionLoadLog => Path.Combine(Root, "extension-load.log");
    public int Port { get; private set; }
    public string UserCacheRoot => Path.Combine(AgentDirectory, "git", "127.0.0.1", "fixture", "repo");
    private Process? _daemon;
    private Task<string>? _daemonOutput;
    private Task<string>? _daemonError;

    public async Task CreateAsync()
    {
        Directory.CreateDirectory(AgentDirectory);
        Directory.CreateDirectory(ProjectDirectory);
        Directory.CreateDirectory(Path.Combine(Root, "tarballs"));
        Directory.CreateDirectory(Path.Combine(ExportDirectory, "fixture"));

        var dependencyDirectory = Path.Combine(Root, "dependency-package");
        Directory.CreateDirectory(dependencyDirectory);
        await File.WriteAllTextAsync(Path.Combine(dependencyDirectory, "package.json"), JsonSerializer.Serialize(new
        {
            name = "managed-git-fixture-dependency",
            version = "1.0.0",
            scripts = new { postinstall = "node postinstall.js" }
        }));
        await File.WriteAllTextAsync(Path.Combine(dependencyDirectory, "postinstall.js"),
            "if (process.env.PISHARP_TEST_GIT_FAIL_DEPENDENCY === '1') process.exit(23);\n" +
            "require('node:fs').appendFileSync(process.env.PISHARP_TEST_GIT_DEPENDENCY_LOG, 'run\\n');\n");
        var dependencyPack = await RunNpmPackAsync(dependencyDirectory);
        var dependencyTarball = dependencyPack;

        var sourceDirectory = Path.Combine(Root, "source");
        Directory.CreateDirectory(sourceDirectory);
        await RunGitAsync(sourceDirectory, ["init", "--initial-branch=main"]);
        var vendor = Path.Combine(sourceDirectory, "vendor");
        Directory.CreateDirectory(vendor);
        File.Copy(dependencyTarball, Path.Combine(vendor, Path.GetFileName(dependencyTarball)));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "package.json"), JsonSerializer.Serialize(new
        {
            name = "managed-git-resource-package",
            version = "1.0.0",
            pi = new
            {
                skills = new[] { "skills" },
                prompts = new[] { "prompts" },
                themes = new[] { "themes" },
                extensions = new[] { "extensions/resource-discovery.dll" }
            },
            dependencies = new Dictionary<string, string>
            {
                ["managed-git-fixture-dependency"] = "file:vendor/managed-git-fixture-dependency-1.0.0.tgz"
            },
            peerDependencies = new Dictionary<string, string> { ["missing-host-peer"] = "^1.0.0" },
            scripts = new { postinstall = "node postinstall.js" }
        }, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "postinstall.js"),
            "require('node:fs').appendFileSync(process.env.PISHARP_TEST_GIT_PACKAGE_LOG, 'run\\n');\n");
        await WriteVersionFilesAsync(sourceDirectory, "git-guide-v1", "Review from Git v1.");
        var extensionDirectory = Path.Combine(sourceDirectory, "extensions");
        Directory.CreateDirectory(extensionDirectory);
        File.Copy(typeof(ResourceDiscoveryFixture).Assembly.Location,
            Path.Combine(extensionDirectory, "resource-discovery.dll"));
        await RunGitAsync(sourceDirectory, ["add", "."]);
        await RunGitAsync(sourceDirectory, ["-c", "user.name=PiSharp Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-m", "v1"]);
        await RunGitAsync(sourceDirectory, ["tag", "v1"]);
        var version1 = (await RunGitAsync(sourceDirectory, ["rev-parse", "HEAD"])).Trim();

        Directory.Delete(Path.Combine(sourceDirectory, "skills"), recursive: true);
        Directory.Delete(Path.Combine(sourceDirectory, "prompts"), recursive: true);
        Directory.Delete(Path.Combine(sourceDirectory, "themes"), recursive: true);
        await WriteVersionFilesAsync(sourceDirectory, "git-guide-v2", "Review from Git v2.");
        await RunGitAsync(sourceDirectory, ["add", "."]);
        await RunGitAsync(sourceDirectory, ["-c", "user.name=PiSharp Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-m", "v2"]);
        await RunGitAsync(sourceDirectory, ["tag", "v2"]);
        var version2 = (await RunGitAsync(sourceDirectory, ["rev-parse", "HEAD"])).Trim();

        await RunGitAsync(sourceDirectory, ["clone", "--bare", sourceDirectory, RepositoryDirectory]);
        Version1 = version1;
        Version2 = version2;
        Port = AllocatePort();
        await StartDaemonAsync();
    }

    public string Source(string reference) => $"git:git://127.0.0.1:{Port}/fixture/repo.git@{reference}";
    public string UnpinnedSource => $"git:git://127.0.0.1:{Port}/fixture/repo.git";

    public async Task SetRemoteDefaultBranchAsync(string commit)
    {
        await RunGitAsync(ExportDirectory, ["--git-dir", RepositoryDirectory, "update-ref", "refs/heads/main", commit]);
        await RunGitAsync(ExportDirectory, ["--git-dir", RepositoryDirectory, "symbolic-ref", "HEAD", "refs/heads/main"]);
    }

    public Dictionary<string, string?> CreateProcessEnvironment() => new(StringComparer.Ordinal)
    {
        ["npm_config_offline"] = "true",
        ["npm_config_cache"] = NpmCacheDirectory,
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "gitconfig"),
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["PISHARP_TEST_GIT_PACKAGE_LOG"] = PackageInstallLog,
        ["PISHARP_TEST_GIT_DEPENDENCY_LOG"] = DependencyInstallLog,
        ["PISHARP_TEST_EXTENSION_LOAD_LOG"] = ExtensionLoadLog,
        ["PISHARP_TEST_RESOURCE_DISCOVERY"] = "true"
    };

    public async Task StartDaemonAsync()
    {
        if (_daemon is { HasExited: false }) return;
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = ExportDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("daemon");
        start.ArgumentList.Add("--reuseaddr");
        start.ArgumentList.Add("--export-all");
        start.ArgumentList.Add("--base-path=" + ExportDirectory);
        start.ArgumentList.Add("--listen=127.0.0.1");
        start.ArgumentList.Add("--port=" + Port);
        ApplyGitEnvironment(start);
        _daemon = Process.Start(start) ?? throw new IOException("Could not start local git daemon.");
        _daemonOutput = _daemon.StandardOutput.ReadToEndAsync();
        _daemonError = _daemon.StandardError.ReadToEndAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_daemon.HasExited)
                throw new IOException($"Local git daemon exited: {await _daemonError}");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException) { await Task.Delay(50); }
        }
        throw new TimeoutException("Local git daemon did not start listening.");
    }

    public async Task StopDaemonAsync()
    {
        if (_daemon is null || _daemon.HasExited) return;
        _daemon.Kill(entireProcessTree: true);
        await _daemon.WaitForExitAsync();
        if (_daemonOutput is not null) await _daemonOutput;
        if (_daemonError is not null) await _daemonError;
        _daemon.Dispose();
        _daemon = null;
    }

    public string ReadBuiltInTheme(string name)
    {
        using var resource = typeof(TerminalTheme).Assembly.GetManifestResourceStream("PiSharp.Cli.Tui.Themes.dark.json");
        if (resource is null) throw new InvalidOperationException("Built-in theme resource is unavailable.");
        using var reader = new StreamReader(resource);
        var theme = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        theme["name"] = name;
        return theme.ToJsonString();
    }

    public string Version1 { get; private set; } = "";
    public string Version2 { get; private set; } = "";

    private async Task WriteVersionFilesAsync(string sourceDirectory, string skillName, string prompt)
    {
        var skillDirectory = Path.Combine(sourceDirectory, "skills", skillName);
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"),
            $"---\nname: {skillName}\ndescription: Managed Git resource\n---\nUse {skillName}.");
        var promptsDirectory = Path.Combine(sourceDirectory, "prompts");
        Directory.CreateDirectory(promptsDirectory);
        await File.WriteAllTextAsync(Path.Combine(promptsDirectory, "review.md"), prompt);
        var themesDirectory = Path.Combine(sourceDirectory, "themes");
        Directory.CreateDirectory(themesDirectory);
        await File.WriteAllTextAsync(Path.Combine(themesDirectory, "managed.json"), ReadBuiltInTheme("managed-git-theme"));
    }

    private async Task<string> RunNpmPackAsync(string packageDirectory)
    {
        var packDirectory = Path.Combine(Root, "tarballs");
        var start = new ProcessStartInfo
        {
            FileName = "npm",
            WorkingDirectory = packageDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("pack");
        start.ArgumentList.Add("--pack-destination");
        start.ArgumentList.Add(packDirectory);
        start.ArgumentList.Add("--ignore-scripts");
        start.Environment["npm_config_offline"] = "true";
        start.Environment["npm_config_cache"] = NpmCacheDirectory;
        using var process = Process.Start(start) ?? throw new IOException("Could not start npm pack.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException($"npm pack failed: {error}\n{output}");
        return Path.Combine(packDirectory, "managed-git-fixture-dependency-1.0.0.tgz");
    }

    private async Task<string> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        ApplyGitEnvironment(start);
        using var process = Process.Start(start) ?? throw new IOException("Could not start git fixture command.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException($"git {string.Join(' ', arguments)} failed: {error}\n{output}");
        return output;
    }

    private void ApplyGitEnvironment(ProcessStartInfo start)
    {
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "gitconfig");
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }

    private static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopDaemonAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
