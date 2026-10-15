using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace PiSharp.Runtime.Resources;

/// <summary>A package source's installed root. Source remains the lexical settings value.</summary>
public sealed record ManagedResourcePackageRoot(string Source, string Scope, string Path);

/// <summary>Acquires managed npm and Git resource packages into user or project caches.</summary>
public sealed partial class ManagedResourcePackageStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_installLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly char[] s_javascriptWhitespace = ['\t', '\n', '\v', '\f', '\r', ' ', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009',
        '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF'];
    private readonly string _agentDirectory;
    private readonly IReadOnlyDictionary<string, string?> _processEnvironmentOverrides;

    public ManagedResourcePackageStore(string agentDirectory)
        : this(agentDirectory, null) { }

    internal ManagedResourcePackageStore(string agentDirectory, IReadOnlyDictionary<string, string?>? processEnvironmentOverrides)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectory);
        _agentDirectory = Path.GetFullPath(agentDirectory);
        _processEnvironmentOverrides = processEnvironmentOverrides ?? new Dictionary<string, string?>();
    }

    /// <summary>Installs missing user packages and trusted project packages, returning roots for discovery.</summary>
    public async Task<IReadOnlyList<ManagedResourcePackageRoot>> ResolveAsync(string workingDirectory, bool trusted,
        IReadOnlyList<ResourcePackageSource>? userPackages = null,
        IReadOnlyList<ResourcePackageSource>? projectPackages = null,
        bool offline = false,
        CancellationToken cancellationToken = default)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        var roots = new List<ManagedResourcePackageRoot>();
        var selectedUserPackages = SelectScopeDeclarations(userPackages ?? [], "user");
        await ResolveScopeAsync(cwd, selectedUserPackages, "user", Path.Combine(_agentDirectory, "npm"), roots,
            offline, _processEnvironmentOverrides, cancellationToken);
        await ResolveGitScopeAsync(cwd, selectedUserPackages, "user", Path.Combine(_agentDirectory, "git"), roots,
            offline, cancellationToken);
        if (trusted)
        {
            var projectPackagesToResolve = new List<ResourcePackageSource>();
            foreach (var projectPackage in SelectScopeDeclarations(projectPackages ?? [], "project"))
            {
                if (!projectPackage.Autoload && TryGetManagedPackageIdentity(projectPackage.Source, out var projectIdentity))
                {
                    var userPackage = selectedUserPackages.FirstOrDefault(candidate =>
                        TryGetManagedPackageIdentity(candidate.Source, out var userIdentity) && userIdentity == projectIdentity);
                    if (userPackage is null)
                    {
                        projectPackagesToResolve.Add(projectPackage);
                        continue;
                    }
                    var userRoot = userPackage is null ? null : roots.FirstOrDefault(candidate =>
                        candidate.Scope == "user" && candidate.Source == userPackage.Source);
                    if (userRoot is not null)
                    {
                        // Pi resolves an autoload:false project declaration from the matching user install,
                        // while retaining the project's lexical source and filter for resource metadata.
                        roots.Add(new(projectPackage.Source, "project", userRoot.Path));
                    }
                    // A matching project delta never acquires a separate project copy, including when
                    // its user package is absent or intentionally unresolved offline.
                    continue;
                }
                projectPackagesToResolve.Add(projectPackage);
            }
            await ResolveScopeAsync(cwd, projectPackagesToResolve, "project", Path.Combine(cwd, ".pi", "npm"), roots,
                offline, _processEnvironmentOverrides, cancellationToken);
            await ResolveGitScopeAsync(cwd, projectPackagesToResolve, "project", Path.Combine(cwd, ".pi", "git"), roots,
                offline, cancellationToken);
        }
        return roots;
    }

    private static IReadOnlyList<ResourcePackageSource> SelectScopeDeclarations(
        IReadOnlyList<ResourcePackageSource> packages, string scope)
    {
        var result = new List<ResourcePackageSource>();
        var managedIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            if (!TryGetManagedPackageIdentity(package.Source, out var identity))
            {
                result.Add(package);
                continue;
            }
            if (!managedIndexes.TryGetValue(identity, out var index))
            {
                managedIndexes.Add(identity, result.Count);
                result.Add(package);
            }
            else if (scope == "project")
            {
                result[index] = package;
            }
        }
        return result;
    }

    private static async Task ResolveScopeAsync(string workingDirectory, IReadOnlyList<ResourcePackageSource>? packages, string scope,
        string installRoot, List<ManagedResourcePackageRoot> roots, bool offline,
        IReadOnlyDictionary<string, string?> processEnvironmentOverrides, CancellationToken cancellationToken)
    {
        foreach (var package in packages ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseNpmSource(package.Source, out var spec)) continue;
            var packageRoot = Path.GetFullPath(Path.Combine(installRoot, "node_modules", spec.Name.Replace('/', Path.DirectorySeparatorChar)));
            var separator = Path.GetFullPath(Path.Combine(installRoot, "node_modules")) + Path.DirectorySeparatorChar;
            if (!packageRoot.StartsWith(separator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid npm package name in source '{package.Source}'.");

            if (!await IsInstalledAsync(workingDirectory, installRoot, packageRoot, spec,
                    processEnvironmentOverrides, cancellationToken))
            {
                if (offline) continue;
                await InstallAsync(workingDirectory, installRoot, package.Source, spec,
                    processEnvironmentOverrides, cancellationToken);
            }
            if (!Directory.Exists(packageRoot))
                throw new InvalidDataException($"npm install completed without installing package '{spec.Name}'.");
            roots.Add(new(package.Source, scope, packageRoot));
        }
    }

    private static async Task<bool> IsInstalledAsync(string workingDirectory, string installRoot, string packageRoot, NpmPackageSpec spec,
        IReadOnlyDictionary<string, string?> processEnvironmentOverrides,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifestPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(manifestPath)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                name.GetString() != spec.Name)
                return false;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(version.GetString())) return false;
            if (spec.Version is null) return true;
            if (IsNonRangeNpmSpec(spec.Version)) return true;
            var (exitCode, output, _) = await RunNpmAsync(workingDirectory,
                ["ls", "--json", "--depth=0", "--omit=peer", "--prefix", installRoot, spec.Name + "@" + spec.Version],
                processEnvironmentOverrides, cancellationToken);
            if (exitCode != 0) return false;
            using var packageTree = JsonDocument.Parse(output);
            var tree = packageTree.RootElement;
            return tree.ValueKind == JsonValueKind.Object && tree.TryGetProperty("dependencies", out var dependencies) &&
                dependencies.ValueKind == JsonValueKind.Object && dependencies.TryGetProperty(spec.Name, out var dependency) &&
                dependency.ValueKind == JsonValueKind.Object && dependency.TryGetProperty("version", out var installedVersion) &&
                installedVersion.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(installedVersion.GetString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static async Task InstallAsync(string workingDirectory, string installRoot, string source, NpmPackageSpec spec,
        IReadOnlyDictionary<string, string?> processEnvironmentOverrides,
        CancellationToken cancellationToken)
    {
        var rootKey = Path.GetFullPath(installRoot);
        var installLock = s_installLocks.GetOrAdd(rootKey, _ => new SemaphoreSlim(1, 1));
        await installLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(installRoot);
            var gitIgnore = Path.Combine(installRoot, ".gitignore");
            if (!File.Exists(gitIgnore)) await File.WriteAllTextAsync(gitIgnore, "*\n!.gitignore\n", cancellationToken);
            var projectManifest = Path.Combine(installRoot, "package.json");
            if (!File.Exists(projectManifest))
                await File.WriteAllTextAsync(projectManifest, "{\"name\":\"pi-extensions\",\"private\":true}\n", cancellationToken);

            // Another resolution may have populated this root while this call waited.
            if (await IsInstalledAsync(workingDirectory, installRoot,
                    Path.Combine(installRoot, "node_modules", spec.Name.Replace('/', Path.DirectorySeparatorChar)),
                    spec, processEnvironmentOverrides, cancellationToken)) return;

            cancellationToken.ThrowIfCancellationRequested();
            var (exitCode, output, errorOutput) = await RunNpmAsync(workingDirectory,
                ["install", spec.InstallSpec, "--prefix", installRoot, "--legacy-peer-deps"],
                processEnvironmentOverrides, cancellationToken);
            if (exitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(errorOutput) ? output.Trim() : errorOutput.Trim();
                throw new IOException($"npm install failed for '{source}' (exit {exitCode}). {details}");
            }
        }
        finally
        {
            installLock.Release();
        }
    }

    private static bool IsNonRangeNpmSpec(string version)
    {
        if (version.StartsWith("file:", StringComparison.Ordinal) ||
            version.StartsWith("npm:", StringComparison.Ordinal) || version == "latest") return true;
        var candidate = version.Trim();
        if (candidate.Length == 0) return false;
        var first = candidate[0];
        // Dist-tags can contain digits (for example beta1 or next-2). Identify only the
        // usual semver range starts here; npm owns actual range satisfaction below.
        if (char.IsAsciiDigit(first) || first is '^' or '~' or '>' or '<' or '=' or '*') return false;
        if (first is 'x' or 'X') return candidate.Length > 1 && candidate[1] is not ('.' or ' ' or '\t');
        if (first is 'v' or 'V') return candidate.Length == 1 || !char.IsAsciiDigit(candidate[1]);
        return true;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunNpmAsync(
        string workingDirectory, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> processEnvironmentOverrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "npm",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var (key, value) in processEnvironmentOverrides)
            process.StartInfo.Environment[key] = value;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new IOException("Could not start npm.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new IOException($"Could not start npm: {error.Message}", error);
        }

        var standardOutput = ReadBoundedAsync(process.StandardOutput);
        var standardError = ReadBoundedAsync(process.StandardError);
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

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 64 * 1024;
        var buffer = new char[4096];
        var output = new System.Text.StringBuilder(limit);
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory());
            if (count == 0) break;
            var retained = Math.Min(count, limit - output.Length);
            if (retained > 0) output.Append(buffer, 0, retained);
            if (retained < count) truncated = true;
        }
        if (truncated) output.Append("\n... npm output truncated");
        return output.ToString();
    }

    private static bool TryParseNpmSource(string source, out NpmPackageSpec spec)
    {
        spec = default!;
        var trimmed = source.Trim(s_javascriptWhitespace);
        if (!trimmed.StartsWith("npm:", StringComparison.Ordinal)) return false;
        var installSpec = trimmed["npm:".Length..].Trim(s_javascriptWhitespace);
        if (installSpec.Length == 0) throw new InvalidDataException("npm package sources must include a package name.");
        var versionIndex = installSpec.StartsWith('@')
            ? installSpec.IndexOf('@', installSpec.IndexOf('/') + 1)
            : installSpec.IndexOf('@');
        var name = versionIndex < 0 ? installSpec : installSpec[..versionIndex];
        var version = versionIndex < 0 ? null : installSpec[(versionIndex + 1)..];
        if (!IsValidPackageName(name) || version is { Length: 0 })
            throw new InvalidDataException($"Invalid npm package source '{source}'.");
        spec = new(name, version, installSpec);
        return true;
    }

    internal static bool TryGetNpmPackageName(string source, out string name)
    {
        if (TryParseNpmSource(source, out var spec))
        {
            name = spec.Name;
            return true;
        }
        name = "";
        return false;
    }

    internal static bool TryGetGitIdentity(string source, out string identity)
    {
        if (ManagedGitSource.TryParse(source, out var git))
        {
            identity = git.Identity;
            return true;
        }
        identity = "";
        return false;
    }

    internal static bool TryGetManagedPackageIdentity(string source, out string identity)
    {
        if (TryGetNpmPackageName(source, out var name))
        {
            identity = $"npm:{name}";
            return true;
        }
        return TryGetGitIdentity(source, out identity);
    }

    private static bool IsValidPackageName(string name)
    {
        static bool SegmentIsSafe(string segment) => segment.Length > 0 && segment is not ("." or "..") &&
            segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~');
        if (name.StartsWith('@'))
        {
            var parts = name[1..].Split('/');
            return parts.Length == 2 && SegmentIsSafe(parts[0]) && SegmentIsSafe(parts[1]);
        }
        return !name.Contains('/') && SegmentIsSafe(name);
    }

    private sealed record NpmPackageSpec(string Name, string? Version, string InstallSpec);
}
