using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Resources;

public sealed partial class ManagedResourcePackageStore
{
    /// <summary>Installs missing configured packages and explicitly reconciles selected Git refs.</summary>
    public async Task<IReadOnlyList<ManagedResourcePackageRoot>> InstallAsync(string workingDirectory, bool trusted,
        IReadOnlyList<ResourcePackageSource>? userPackages = null,
        IReadOnlyList<ResourcePackageSource>? projectPackages = null,
        string? source = null,
        bool offline = false,
        CancellationToken cancellationToken = default, TextWriter? standardOutput = null,
        TextWriter? standardError = null)
    {
        var targets = GetInstallTargets(workingDirectory, userPackages, projectPackages, trusted, source);
        if (offline) return await ResolveAsync(workingDirectory, trusted, userPackages, projectPackages,
            offline: true, cancellationToken);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ManagedGitSource.TryParse(target.Package.Source, out var git))
                await ReconcileGitAsync(target.WorkingDirectory, target.GitInstallRoot, git, cancellationToken,
                    standardOutput, standardError);
            else if (TryParseNpmSource(target.Package.Source, out var npm))
                await InstallAsync(target.WorkingDirectory, target.NpmInstallRoot, target.Package.Source, npm,
                    _processEnvironmentOverrides, cancellationToken, standardOutput: standardOutput,
                    standardError: standardError);
        }
        return await ResolveAsync(workingDirectory, trusted, userPackages, projectPackages,
            offline: true, cancellationToken);
    }

    /// <summary>Updates eligible configured packages, including configured Git refs, and returns discovery roots.</summary>
    public async Task<IReadOnlyList<ManagedResourcePackageRoot>> UpdateAsync(string workingDirectory, bool trusted,
        IReadOnlyList<ResourcePackageSource>? userPackages = null,
        IReadOnlyList<ResourcePackageSource>? projectPackages = null,
        string? source = null,
        bool offline = false,
        CancellationToken cancellationToken = default, TextWriter? standardOutput = null,
        TextWriter? standardError = null)
    {
        var targets = GetUpdateTargets(workingDirectory, userPackages, projectPackages, trusted, source);
        if (offline) return await ResolveAsync(workingDirectory, trusted, userPackages, projectPackages,
            offline: true, cancellationToken);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ManagedGitSource.TryParse(target.Package.Source, out var git))
            {
                await ReconcileGitAsync(target.WorkingDirectory, target.GitInstallRoot, git, cancellationToken);
            }
            else if (TryParseNpmSource(target.Package.Source, out var npm) && !IsExactNpmVersion(npm.Version))
            {
                if (!await HasNpmPackageAsync(target.NpmInstallRoot, npm, cancellationToken))
                {
                    await InstallAsync(target.WorkingDirectory, target.NpmInstallRoot, target.Package.Source, npm,
                        _processEnvironmentOverrides, cancellationToken, standardOutput: standardOutput,
                        standardError: standardError);
                }
                else if (await NpmHasAvailableUpdateAsync(target.WorkingDirectory, target.NpmInstallRoot, npm, cancellationToken))
                {
                    await UpdateNpmAsync(target.WorkingDirectory, target.NpmInstallRoot, target.Package.Source,
                        npm, cancellationToken, standardOutput, standardError);
                }
            }
        }
        return await ResolveAsync(workingDirectory, trusted, userPackages, projectPackages,
            offline: true, cancellationToken);
    }

    private IReadOnlyList<PackageUpdateTarget> GetUpdateTargets(string workingDirectory,
        IReadOnlyList<ResourcePackageSource>? userPackages,
        IReadOnlyList<ResourcePackageSource>? projectPackages, bool trusted, string? source)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        var candidates = new List<PackageUpdateTarget>();
        candidates.AddRange((userPackages ?? []).Select(package => CreateTarget(package, "user")));
        if (trusted) candidates.AddRange((projectPackages ?? []).Select(package => CreateTarget(package, "project")));

        if (source is null) return candidates;
        var requestedIdentity = TryGetManagedPackageIdentity(source, out var managedIdentity)
            ? managedIdentity
            : source.Trim();
        var selected = candidates.Where(candidate =>
            TryGetManagedPackageIdentity(candidate.Package.Source, out var candidateIdentity)
                ? candidateIdentity == requestedIdentity
                : string.Equals(candidate.Package.Source.Trim(), requestedIdentity, StringComparison.Ordinal)).ToArray();
        if (selected.Length == 0)
            throw new InvalidDataException($"No configured package matches '{source}'.");
        return selected;

        PackageUpdateTarget CreateTarget(ResourcePackageSource package, string scope) => new(package, scope,
            cwd,
            Path.Combine(_agentDirectory, "npm"), Path.Combine(cwd, ".pi", "npm"),
            Path.Combine(_agentDirectory, "git"), Path.Combine(cwd, ".pi", "git"));
    }

    private IReadOnlyList<PackageUpdateTarget> GetInstallTargets(string workingDirectory,
        IReadOnlyList<ResourcePackageSource>? userPackages,
        IReadOnlyList<ResourcePackageSource>? projectPackages, bool trusted, string? source)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        var users = SelectScopeDeclarations(userPackages ?? [], "user");
        var projects = trusted ? SelectScopeDeclarations(projectPackages ?? [], "project") : [];
        var candidates = users.Select(package => CreateTarget(package, "user")).ToList();
        foreach (var project in projects)
        {
            if (!project.Autoload && TryGetManagedPackageIdentity(project.Source, out var identity) &&
                users.Any(user => TryGetManagedPackageIdentity(user.Source, out var userIdentity) && userIdentity == identity))
                continue;
            candidates.Add(CreateTarget(project, "project"));
        }
        if (source is null) return candidates;
        var requestedIdentity = TryGetManagedPackageIdentity(source, out var managedIdentity) ? managedIdentity : source.Trim();
        var selected = candidates.Where(candidate =>
            TryGetManagedPackageIdentity(candidate.Package.Source, out var candidateIdentity)
                ? candidateIdentity == requestedIdentity
                : string.Equals(candidate.Package.Source.Trim(), requestedIdentity, StringComparison.Ordinal)).ToArray();
        if (selected.Length == 0) throw new InvalidDataException($"No configured package matches '{source}'.");
        return selected;

        PackageUpdateTarget CreateTarget(ResourcePackageSource package, string scope) => new(package, scope,
            cwd,
            Path.Combine(_agentDirectory, "npm"), Path.Combine(cwd, ".pi", "npm"),
            Path.Combine(_agentDirectory, "git"), Path.Combine(cwd, ".pi", "git"));
    }

    private async Task ResolveGitScopeAsync(string workingDirectory, IReadOnlyList<ResourcePackageSource> packages,
        string scope, string installRoot, List<ManagedResourcePackageRoot> roots, bool offline,
        CancellationToken cancellationToken)
    {
        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ManagedGitSource.TryParse(package.Source, out var git)) continue;
            var packageRoot = GetGitPackageRoot(installRoot, git);
            if (!Directory.Exists(packageRoot))
            {
                if (offline) continue;
                await AcquireGitAsync(workingDirectory, packageRoot, git, installRoot,
                    reconcileExisting: false, cancellationToken);
            }
            if (Directory.Exists(packageRoot)) roots.Add(new(package.Source, scope, packageRoot));
        }
    }

    private async Task ReconcileGitAsync(string workingDirectory, string installRoot, ManagedGitSource source,
        CancellationToken cancellationToken, TextWriter? standardOutput = null,
        TextWriter? standardError = null)
    {
        var packageRoot = GetGitPackageRoot(installRoot, source);
        await AcquireGitAsync(workingDirectory, packageRoot, source, installRoot,
            reconcileExisting: true, cancellationToken, standardOutput, standardError);
    }

    private static string GetGitPackageRoot(string installRoot, ManagedGitSource source)
    {
        var root = Path.GetFullPath(installRoot);
        var packageRoot = Path.GetFullPath(Path.Combine(root, source.Host,
            Path.Combine(source.Path.Split('/'))));
        var prefix = root + Path.DirectorySeparatorChar;
        if (!packageRoot.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid Git package source identity '{source.Identity}'.");
        return packageRoot;
    }

    private async Task AcquireGitAsync(string workingDirectory, string packageRoot, ManagedGitSource source,
        string gitInstallRoot, bool reconcileExisting, CancellationToken cancellationToken,
        TextWriter? standardOutput = null, TextWriter? standardError = null)
    {
        var rootKey = Path.GetFullPath(packageRoot);
        var installLock = s_installLocks.GetOrAdd(rootKey, _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync(cancellationToken);
        string? stage = null;
        string? backup = null;
        try
        {
            var existed = Directory.Exists(packageRoot);
            if (existed && !reconcileExisting) return;
            await EnsureGitIgnoreAsync(gitInstallRoot, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(packageRoot)!);
            stage = packageRoot + ".pisharp-stage-" + Guid.NewGuid().ToString("N");
            var branch = existed && source.Ref is null
                ? await GetCurrentBranchAsync(packageRoot, cancellationToken)
                : null;
            var cloneArguments = new List<string> { "clone" };
            if (!string.IsNullOrWhiteSpace(branch))
            {
                ValidateGitReference(branch);
                cloneArguments.Add("--branch");
                cloneArguments.Add(branch);
            }
            cloneArguments.Add(source.Repository);
            cloneArguments.Add(stage);
            await RunGitAsync(workingDirectory, cloneArguments, cancellationToken,
                standardOutput: standardOutput, standardError: standardError);

            if (source.Ref is not null)
            {
                ValidateGitReference(source.Ref);
                await RunGitAsync(stage, ["-C", stage, "fetch", "origin", source.Ref], cancellationToken,
                    standardOutput: standardOutput, standardError: standardError);
                await RunGitAsync(stage, ["-C", stage, "checkout", "--detach", "FETCH_HEAD"], cancellationToken,
                    standardOutput: standardOutput, standardError: standardError);
            }

            var stagedHead = (await RunGitAsync(stage, ["-C", stage, "rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
            if (existed)
            {
                var existingHead = await TryGetHeadAsync(packageRoot, cancellationToken);
                if (existingHead is not null && existingHead == stagedHead && !HasMissingGitDependencies(packageRoot)) return;
            }

            if (File.Exists(Path.Combine(stage, "package.json")))
            {
                var (exitCode, output, errorOutput) = await RunNpmAsync(stage,
                    GetGitDependencyInstallArguments(), _processEnvironmentOverrides, cancellationToken,
                    standardOutput, standardError);
                if (exitCode != 0)
                {
                    var details = string.IsNullOrWhiteSpace(errorOutput) ? output.Trim() : errorOutput.Trim();
                    throw new IOException($"npm install failed for Git package '{source.Identity}' (exit {exitCode}). {details}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (existed)
            {
                backup = packageRoot + ".pisharp-backup-" + Guid.NewGuid().ToString("N");
                Directory.Move(packageRoot, backup);
            }
            try
            {
                Directory.Move(stage, packageRoot);
                stage = null;
            }
            catch
            {
                if (backup is not null && Directory.Exists(backup) && !Directory.Exists(packageRoot))
                    Directory.Move(backup, packageRoot);
                backup = null;
                throw;
            }
            if (backup is not null)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                backup = null;
            }
        }
        finally
        {
            if (stage is not null) DeleteDirectory(stage);
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(packageRoot))
            {
                try { Directory.Move(backup, packageRoot); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            installLock.Release();
        }
    }

    private async Task<string?> GetCurrentBranchAsync(string packageRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(packageRoot, ["-C", packageRoot, "symbolic-ref", "--quiet", "--short", "HEAD"],
            cancellationToken, throwOnFailure: false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private async Task<string?> TryGetHeadAsync(string packageRoot, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(packageRoot, ["-C", packageRoot, "rev-parse", "HEAD"],
            cancellationToken, throwOnFailure: false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private static bool HasMissingGitDependencies(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(manifestPath)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("dependencies", out var dependencies) ||
                dependencies.ValueKind != JsonValueKind.Object) return false;
            var nodeModules = Path.Combine(packageRoot, "node_modules");
            return dependencies.EnumerateObject().Any(dependency =>
                !Directory.Exists(Path.Combine(nodeModules, dependency.Name.Replace('/', Path.DirectorySeparatorChar))));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task UpdateNpmAsync(string workingDirectory, string installRoot, string source, NpmPackageSpec spec,
        CancellationToken cancellationToken, TextWriter? standardOutput, TextWriter? standardError)
    {
        var installLock = s_installLocks.GetOrAdd(Path.GetFullPath(installRoot), _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync(cancellationToken);
        try
        {
            var installSpec = spec.Version is null ? spec.Name + "@latest" : spec.InstallSpec;
            var (exitCode, output, errorOutput) = await RunNpmAsync(workingDirectory,
                GetNpmInstallArguments(installSpec, installRoot),
                _processEnvironmentOverrides, cancellationToken, standardOutput, standardError);
            if (exitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(errorOutput) ? output.Trim() : errorOutput.Trim();
                throw new IOException($"npm update failed for '{source}' (exit {exitCode}). {details}");
            }
        }
        finally { installLock.Release(); }
    }

    private async Task<bool> HasNpmPackageAsync(string installRoot, NpmPackageSpec spec, CancellationToken cancellationToken)
    {
        var packageRoot = Path.Combine(installRoot, "node_modules", spec.Name.Replace('/', Path.DirectorySeparatorChar));
        var manifestPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(manifestPath)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            return manifest.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                name.GetString() == spec.Name && manifest.RootElement.TryGetProperty("version", out var version) &&
                version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private async Task<bool> NpmHasAvailableUpdateAsync(string workingDirectory, string installRoot,
        NpmPackageSpec spec, CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "pisharp-npm-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var checkSpec = spec.Version ?? "latest";
            var manifest = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = "pisharp-managed-update-check",
                ["private"] = true,
                ["dependencies"] = new Dictionary<string, string> { [spec.Name] = checkSpec }
            });
            await File.WriteAllTextAsync(Path.Combine(temporaryRoot, "package.json"), manifest, cancellationToken);
            var packageRoot = Path.Combine(temporaryRoot, "node_modules", spec.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(packageRoot);
            var installedManifest = Path.Combine(installRoot, "node_modules", spec.Name.Replace('/', Path.DirectorySeparatorChar), "package.json");
            File.Copy(installedManifest, Path.Combine(packageRoot, "package.json"));
            var (exitCode, output, _) = await RunNpmAsync(temporaryRoot,
                ["outdated", spec.Name, "--json", "--prefix", temporaryRoot],
                _processEnvironmentOverrides, cancellationToken);
            if (exitCode == 0) return false;
            using var result = JsonDocument.Parse(output);
            if (result.RootElement.ValueKind != JsonValueKind.Object ||
                !result.RootElement.TryGetProperty(spec.Name, out var outdated) || outdated.ValueKind != JsonValueKind.Object ||
                !outdated.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.String ||
                !outdated.TryGetProperty("wanted", out var wanted) || wanted.ValueKind != JsonValueKind.String)
                return true;
            return CompareSemanticVersions(wanted.GetString()!, current.GetString()!) is { } comparison
                ? comparison > 0
                : true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // Pi preserves its historical update behavior when its remote version lookup fails.
            return true;
        }
        finally { DeleteDirectory(temporaryRoot); }
    }

    private async Task EnsureGitIgnoreAsync(string gitInstallRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(gitInstallRoot);
        var gitIgnore = Path.Combine(gitInstallRoot, ".gitignore");
        if (File.Exists(gitIgnore)) return;
        try
        {
            await using var stream = new FileStream(gitIgnore, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 256, useAsync: true);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("*\n!.gitignore\n"), cancellationToken);
        }
        catch (IOException) when (File.Exists(gitIgnore)) { }
    }

    private static int? CompareSemanticVersions(string left, string right)
    {
        if (!TryParseSemanticVersion(left, out var leftVersion) || !TryParseSemanticVersion(right, out var rightVersion))
            return null;
        var comparison = leftVersion.Major.CompareTo(rightVersion.Major);
        if (comparison != 0) return comparison;
        comparison = leftVersion.Minor.CompareTo(rightVersion.Minor);
        if (comparison != 0) return comparison;
        comparison = leftVersion.Patch.CompareTo(rightVersion.Patch);
        if (comparison != 0) return comparison;
        if (leftVersion.Prerelease is null) return rightVersion.Prerelease is null ? 0 : 1;
        if (rightVersion.Prerelease is null) return -1;
        var leftParts = leftVersion.Prerelease.Split('.');
        var rightParts = rightVersion.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var leftNumeric = BigInteger.TryParse(leftParts[index], out var leftNumber);
            var rightNumeric = BigInteger.TryParse(rightParts[index], out var rightNumber);
            comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static bool TryParseSemanticVersion(string value, out SemanticVersion version)
    {
        var match = Regex.Match(value,
            "^[v=]?(?<major>0|[1-9]\\d*)\\.(?<minor>0|[1-9]\\d*)\\.(?<patch>0|[1-9]\\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant);
        if (match.Success && BigInteger.TryParse(match.Groups["major"].Value, out var major) &&
            BigInteger.TryParse(match.Groups["minor"].Value, out var minor) &&
            BigInteger.TryParse(match.Groups["patch"].Value, out var patch))
        {
            version = new(major, minor, patch, match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
            return true;
        }
        version = default;
        return false;
    }

    private static bool IsExactNpmVersion(string? version) => version is not null && Regex.IsMatch(version,
        "^[v=]?\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);

    private static void ValidateGitReference(string reference)
    {
        if (reference.Length == 0 || reference[0] == '-' || reference.Any(char.IsControl) ||
            reference.Contains('\\') || reference.Contains('\0') || reference.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"Invalid Git ref '{reference}'.");
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunGitAsync(
        string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        bool throwOnFailure = true, TextWriter? standardOutput = null, TextWriter? standardError = null)
    {
        var result = await RunProcessAsync("git", workingDirectory, arguments, cancellationToken);
        if (standardOutput is not null && result.StandardOutput.Length > 0)
        {
            await standardOutput.WriteAsync(result.StandardOutput);
            await standardOutput.FlushAsync();
        }
        if (standardError is not null && result.StandardError.Length > 0)
        {
            await standardError.WriteAsync(result.StandardError);
            await standardError.FlushAsync();
        }
        if (throwOnFailure && result.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput.Trim()
                : result.StandardError.Trim();
            throw new IOException($"git {string.Join(' ', arguments)} failed (exit {result.ExitCode}). {details}");
        }
        return result;
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string fileName, string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var (key, value) in _processEnvironmentOverrides) process.StartInfo.Environment[key] = value;
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new IOException($"Could not start {fileName}.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException($"Could not start {fileName}: {error.Message}", error);
        }

        var standardOutput = ReadBoundedAsync(process.StandardOutput, fileName);
        var standardError = ReadBoundedAsync(process.StandardError, fileName);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }
        return (process.ExitCode, await standardOutput, await standardError);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, string processName)
    {
        const int limit = 64 * 1024;
        var buffer = new char[4096];
        var output = new StringBuilder(limit);
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            var retained = Math.Min(count, limit - output.Length);
            if (retained > 0) output.Append(buffer, 0, retained);
            if (retained < count) truncated = true;
        }
        if (truncated) output.Append($"\n... {processName} output truncated");
        return output.ToString();
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PackageUpdateTarget(ResourcePackageSource Package, string Scope, string WorkingDirectory,
        string UserNpmInstallRoot, string ProjectNpmInstallRoot, string UserGitInstallRoot, string ProjectGitInstallRoot)
    {
        internal string NpmInstallRoot => Scope == "user" ? UserNpmInstallRoot : ProjectNpmInstallRoot;
        internal string GitInstallRoot => Scope == "user" ? UserGitInstallRoot : ProjectGitInstallRoot;
    }

    private readonly record struct SemanticVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, string? Prerelease);
}
