using System.Diagnostics;
using System.Text.Json;

namespace PiSharp.Tests.TestInfrastructure;

[CollectionDefinition("Managed npm subprocesses", DisableParallelization = true)]
public sealed class ManagedNpmSubprocessCollection { }

internal sealed class ManagedNpmFixture : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-managed-npm-" + Guid.NewGuid().ToString("N"));
    public string AgentDirectory => Path.Combine(Root, "agent");
    public string ProjectDirectory => Path.Combine(Root, "project");
    public string NpmCacheDirectory => Path.Combine(Root, "npm-cache");
    private string TarballDirectory => Path.Combine(Root, "tarballs");

    public void Create()
    {
        Directory.CreateDirectory(AgentDirectory);
        Directory.CreateDirectory(ProjectDirectory);
        Directory.CreateDirectory(TarballDirectory);
    }

    public string CreatePackageDirectory(string name)
    {
        var directory = Path.Combine(Root, "packages", name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public async Task<string> PackAsync(string packageDirectory, string packageJson,
        IReadOnlyDictionary<string, string>? textFiles = null,
        IReadOnlyDictionary<string, string>? copiedFiles = null)
    {
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "package.json"), packageJson);
        foreach (var (relativePath, contents) in textFiles ?? new Dictionary<string, string>())
        {
            var path = Path.GetFullPath(Path.Combine(packageDirectory, relativePath));
            if (!path.StartsWith(Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture package files must stay under the package directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, contents);
        }
        foreach (var (relativePath, sourcePath) in copiedFiles ?? new Dictionary<string, string>())
        {
            var path = Path.GetFullPath(Path.Combine(packageDirectory, relativePath));
            if (!path.StartsWith(Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture package files must stay under the package directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(sourcePath, path, overwrite: true);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "npm",
                WorkingDirectory = packageDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("pack");
        process.StartInfo.ArgumentList.Add("--pack-destination");
        process.StartInfo.ArgumentList.Add(TarballDirectory);
        process.StartInfo.ArgumentList.Add("--ignore-scripts");
        process.StartInfo.Environment["npm_config_offline"] = "true";
        process.StartInfo.Environment["npm_config_cache"] = NpmCacheDirectory;
        if (!process.Start()) throw new IOException("Could not start npm pack.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new IOException($"npm pack failed: {stderr}\n{stdout}");

        using var manifest = JsonDocument.Parse(packageJson);
        var name = manifest.RootElement.GetProperty("name").GetString()!;
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        var tarball = Path.Combine(TarballDirectory, $"{name.Replace('/', '-')}-{version}.tgz");
        if (!File.Exists(tarball)) throw new IOException($"npm pack did not create {tarball}: {stdout}");
        return tarball;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        return ValueTask.CompletedTask;
    }
}
