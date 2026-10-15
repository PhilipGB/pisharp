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
    private IReadOnlyList<string> _npmCommand = [];

    public ManagedResourcePackageStore(string agentDirectory)
        : this(agentDirectory, null) { }

    internal ManagedResourcePackageStore(string agentDirectory, IReadOnlyDictionary<string, string?>? processEnvironmentOverrides)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectory);
        _agentDirectory = Path.GetFullPath(agentDirectory);
        _processEnvironmentOverrides = processEnvironmentOverrides ?? new Dictionary<string, string?>();
    }

    /// <summary>Sets the configured package-manager command as an argv vector.</summary>
    public void ConfigureNpmCommand(IReadOnlyList<string>? command) => _npmCommand = command?.ToArray() ?? [];

    private string PackageManagerName
    {
        get
        {
            if (_npmCommand.Count == 0) return "npm";
            var command = _npmCommand[0];
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidDataException("Invalid npmCommand: first array entry must be a non-empty command");
            static string Normalize(string value) => Path.GetFileName(value.Replace('\\', '/')).Replace(".cmd", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
            var direct = Normalize(command);
            var separator = _npmCommand.ToList().LastIndexOf("--");
            if (separator >= 0)
                return separator + 1 < _npmCommand.Count ? Normalize(_npmCommand[separator + 1]) : direct;
            if (direct is "npm" or "pnpm" or "bun") return direct;
            var managers = _npmCommand.Skip(1).Select(Normalize).Where(name => name is "npm" or "pnpm" or "bun")
                .Distinct(StringComparer.Ordinal).ToArray();
            if (managers.Length > 1)
                throw new InvalidDataException($"Ambiguous npmCommand package managers: {string.Join(", ", managers)}");
            return managers.FirstOrDefault() ?? direct;
        }
    }

    private IReadOnlyList<string> GetNpmInstallArguments(string spec, string installRoot) => PackageManagerName switch
    {
        "bun" => ["install", spec, "--cwd", installRoot, "--omit=peer"],
        "pnpm" => ["install", spec, "--prefix", installRoot, "--config.auto-install-peers=false",
            "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"],
        _ => ["install", spec, "--prefix", installRoot, "--legacy-peer-deps"]
    };

    private IReadOnlyList<string> GetGitDependencyInstallArguments() => PackageManagerName switch
    {
        "bun" => ["install", "--omit=dev", "--omit=peer"],
        "pnpm" => ["install", "--prod", "--config.auto-install-peers=false",
            "--config.strict-peer-dependencies=false", "--config.strict-dep-builds=false"],
        "npm" => ["install", "--omit=dev", "--legacy-peer-deps"],
        _ => ["install"]
    };

    private IReadOnlyList<string> GetNpmUninstallArguments(string packageName, string installRoot) => PackageManagerName switch
    {
        "bun" => ["uninstall", packageName, "--cwd", installRoot],
        "pnpm" => ["uninstall", packageName, "--prefix", installRoot],
        _ => ["uninstall", packageName, "--prefix", installRoot, "--legacy-peer-deps"]
    };

    /// <summary>Returns the selected declaration's installed path without acquiring it.</summary>
    public async Task<string?> GetInstalledPathAsync(string workingDirectory, string source, string scope,
        CancellationToken cancellationToken = default)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        if (TryParseNpmSource(source, out var npm))
        {
            var root = Path.Combine(scope == "project" ? Path.Combine(cwd, ".pi") : _agentDirectory, "npm",
                "node_modules", npm.Name.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(root)) return root;
            if (scope != "user") return null;
            return await GetLegacyGlobalNpmPathAsync(cwd, npm.Name, cancellationToken);
        }
        if (ManagedGitSource.TryParse(source, out var git))
        {
            var root = Path.Combine(scope == "project" ? Path.Combine(cwd, ".pi") : _agentDirectory, "git");
            var path = GetGitPackageRoot(root, git);
            return Directory.Exists(path) ? path : null;
        }
        var baseDirectory = scope == "project" ? Path.Combine(cwd, ".pi") : _agentDirectory;
        var localPath = ResourceLocalPath.Resolve(source, baseDirectory);
        return Directory.Exists(localPath) || File.Exists(localPath) ? localPath : null;
    }

    /// <summary>Removes one selected managed install. Local package sources are references and remain untouched.</summary>
    public async Task RemoveAsync(string workingDirectory, string source, string scope,
        CancellationToken cancellationToken = default)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        var installRoot = Path.Combine(scope == "project" ? Path.Combine(cwd, ".pi") : _agentDirectory, "npm");
        if (TryParseNpmSource(source, out var npm))
        {
            var packageRoot = Path.GetFullPath(Path.Combine(installRoot, "node_modules", npm.Name.Replace('/', Path.DirectorySeparatorChar)));
            var packagePrefix = Path.GetFullPath(Path.Combine(installRoot, "node_modules")) + Path.DirectorySeparatorChar;
            if (!packageRoot.StartsWith(packagePrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid npm package name in source '{source}'.");
            if (Directory.Exists(packageRoot))
            {
                var (exitCode, output, error) = await RunNpmAsync(cwd, GetNpmUninstallArguments(npm.Name, installRoot),
                    _processEnvironmentOverrides, cancellationToken, forwardStandardError: true);
                if (exitCode != 0)
                    throw new IOException($"Package manager uninstall failed for '{source}' (exit {exitCode}). " +
                        (string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim()));
            }
            return;
        }
        if (ManagedGitSource.TryParse(source, out var git))
        {
            var gitRoot = Path.Combine(scope == "project" ? Path.Combine(cwd, ".pi") : _agentDirectory, "git");
            var packageRoot = GetGitPackageRoot(gitRoot, git);
            if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, recursive: true);
        }
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

    /// <summary>Performs the explicit package CLI install/reconcile operation in exactly one scope.</summary>
    public async Task InstallSelectedAsync(string workingDirectory, string source, string scope,
        CancellationToken cancellationToken = default)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        var scopeRoot = scope == "project" ? Path.Combine(cwd, ".pi") : _agentDirectory;
        if (TryParseNpmSource(source, out var npm))
        {
            await InstallAsync(cwd, Path.Combine(scopeRoot, "npm"), source, npm,
                _processEnvironmentOverrides, cancellationToken, force: true);
            return;
        }
        if (ManagedGitSource.TryParse(source, out var git))
            await ReconcileGitAsync(cwd, Path.Combine(scopeRoot, "git"), git, cancellationToken);
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

    private async Task<string?> GetLegacyGlobalNpmPathAsync(string workingDirectory, string packageName,
        CancellationToken cancellationToken)
    {
        try
        {
            string globalRoot;
            if (PackageManagerName == "pnpm")
            {
                var (code, output, _) = await RunNpmAsync(workingDirectory, ["list", "-g", "--depth", "0", "--json"],
                    _processEnvironmentOverrides, cancellationToken);
                if (code == 0 && TryGetPnpmGlobalPath(output, packageName) is { } pnpmPath && Directory.Exists(pnpmPath))
                    return pnpmPath;
            }
            if (PackageManagerName == "bun")
            {
                var (code, output, _) = await RunNpmAsync(workingDirectory, ["pm", "bin", "-g"],
                    _processEnvironmentOverrides, cancellationToken);
                if (code != 0) return null;
                globalRoot = Path.Combine(Path.GetDirectoryName(output.Trim()) ?? "", "install", "global", "node_modules");
            }
            else
            {
                var (code, output, _) = await RunNpmAsync(workingDirectory, ["root", "-g"],
                    _processEnvironmentOverrides, cancellationToken);
                if (code != 0) return null;
                globalRoot = output.Trim();
            }
            var legacyPath = Path.GetFullPath(Path.Combine(globalRoot, packageName.Replace('/', Path.DirectorySeparatorChar)));
            return Directory.Exists(legacyPath) ? legacyPath : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                       System.ComponentModel.Win32Exception or InvalidDataException or ArgumentException or
                                       NotSupportedException)
        {
            // Legacy lookups are compatibility reads; they must not prevent managed acquisition.
            return null;
        }
    }

    private async Task ResolveScopeAsync(string workingDirectory, IReadOnlyList<ResourcePackageSource>? packages, string scope,
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
                if (scope == "user" && await GetLegacyGlobalNpmPathAsync(workingDirectory, spec.Name, cancellationToken) is { } legacyPath)
                {
                    roots.Add(new(package.Source, scope, legacyPath));
                    continue;
                }
                if (offline) continue;
                await InstallAsync(workingDirectory, installRoot, package.Source, spec,
                    processEnvironmentOverrides, cancellationToken);
            }
            if (!Directory.Exists(packageRoot))
                throw new InvalidDataException($"npm install completed without installing package '{spec.Name}'.");
            roots.Add(new(package.Source, scope, packageRoot));
        }
    }

    private async Task<bool> IsInstalledAsync(string workingDirectory, string installRoot, string packageRoot, NpmPackageSpec spec,
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

    private async Task InstallAsync(string workingDirectory, string installRoot, string source, NpmPackageSpec spec,
        IReadOnlyDictionary<string, string?> processEnvironmentOverrides,
        CancellationToken cancellationToken, bool force = false)
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
            if (!force && await IsInstalledAsync(workingDirectory, installRoot,
                    Path.Combine(installRoot, "node_modules", spec.Name.Replace('/', Path.DirectorySeparatorChar)),
                    spec, processEnvironmentOverrides, cancellationToken)) return;

            cancellationToken.ThrowIfCancellationRequested();
            var (exitCode, output, errorOutput) = await RunNpmAsync(workingDirectory,
                GetNpmInstallArguments(spec.InstallSpec, installRoot),
                processEnvironmentOverrides, cancellationToken, forwardStandardError: true);
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

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunNpmAsync(
        string workingDirectory, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> processEnvironmentOverrides, CancellationToken cancellationToken,
        bool forwardStandardError = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _npmCommand.Count == 0 ? "npm" : _npmCommand[0],
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var (key, value) in processEnvironmentOverrides)
            process.StartInfo.Environment[key] = value;
        foreach (var argument in _npmCommand.Skip(1)) process.StartInfo.ArgumentList.Add(argument);
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

        var output = await standardOutput;
        var standardErrorText = await standardError;
        if (forwardStandardError && standardErrorText.Length > 0)
        {
            await Console.Error.WriteAsync(standardErrorText);
            await Console.Error.FlushAsync();
        }
        return (process.ExitCode, output, standardErrorText);
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

    /// <summary>Returns a version-independent npm or canonical Git identity for a managed source.</summary>
    public static string? GetManagedPackageIdentity(string source) =>
        TryGetManagedPackageIdentity(source, out var identity) ? identity : null;

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

    private static string? TryGetPnpmGlobalPath(string output, string packageName)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var entries = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            foreach (var entry in entries)
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("dependencies", out var dependencies) &&
                    dependencies.ValueKind == JsonValueKind.Object && dependencies.TryGetProperty(packageName, out var dependency) &&
                    dependency.ValueKind == JsonValueKind.Object && dependency.TryGetProperty("path", out var path) &&
                    path.ValueKind == JsonValueKind.String)
                    return path.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private sealed record NpmPackageSpec(string Name, string? Version, string InstallSpec);
}
