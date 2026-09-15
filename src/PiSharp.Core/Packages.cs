using System.Text.Json;

namespace PiSharp.Core;

/// <summary>Resource paths declared by a local Pi package manifest.</summary>
public sealed record PiPackageManifest(
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Prompts);

/// <summary>A locally discovered package that contributes Pi resources.</summary>
public sealed record PiPackageDefinition(
    string Name,
    string RootPath,
    PiPackageManifest Manifest);

/// <summary>Results from local package manifest discovery.</summary>
public sealed record PiPackageDiscoveryResult(
    IReadOnlyList<PiPackageDefinition> Packages,
    IReadOnlyList<SkillDiagnostic> Diagnostics)
{
    /// <summary>Gets all package-relative extension directories or files.</summary>
    public IReadOnlyList<string> ExtensionPaths => ResolvePaths(package => package.Manifest.Extensions);

    /// <summary>Gets all package-relative skill directories or files.</summary>
    public IReadOnlyList<string> SkillPaths => ResolvePaths(package => package.Manifest.Skills);

    /// <summary>Gets all package-relative prompt directories or files.</summary>
    public IReadOnlyList<string> PromptPaths => ResolvePaths(package => package.Manifest.Prompts);

    private IReadOnlyList<string> ResolvePaths(Func<PiPackageDefinition, IReadOnlyList<string>> selector) =>
        Packages.SelectMany(package => selector(package).Select(path => Path.GetFullPath(Path.Combine(package.RootPath, path)))).ToArray();
}

/// <summary>
/// Reads package.json manifests without installing or executing package code.
/// Package installation remains host-specific; local packages are intentionally
/// the safe, deterministic subset supported by PiSharp.
/// </summary>
public sealed class PiPackageCatalog
{
    /// <summary>Discovers local packages from the user and workspace package roots.</summary>
    public PiPackageDiscoveryResult Discover(string workspaceRoot, string? homeDirectory = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var packageRoots = new[]
        {
            Path.Combine(home, ".pi", "agent", "packages"),
            Path.Combine(root, ".pi", "packages"),
        };
        var packages = new List<PiPackageDefinition>();
        var diagnostics = new List<SkillDiagnostic>();
        foreach (var packageRoot in packageRoots)
        {
            AddPackages(packageRoot, packages, diagnostics);
        }

        return new PiPackageDiscoveryResult(packages, diagnostics);
    }

    private static void AddPackages(
        string packageRoot,
        ICollection<PiPackageDefinition> packages,
        ICollection<SkillDiagnostic> diagnostics)
    {
        if (!Directory.Exists(packageRoot))
        {
            return;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(packageRoot).OrderBy(path => path, StringComparer.Ordinal);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new SkillDiagnostic(exception.Message, packageRoot));
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new SkillDiagnostic(exception.Message, packageRoot));
            return;
        }

        foreach (var directory in directories)
        {
            var manifestPath = Path.Combine(directory, "package.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var package = ReadPackage(manifestPath, diagnostics);
            if (package is not null)
            {
                packages.Add(package);
            }
        }
    }

    private static PiPackageDefinition? ReadPackage(string manifestPath, ICollection<SkillDiagnostic> diagnostics)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("pi", out var pi) || pi.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var packageRoot = Path.GetDirectoryName(manifestPath)!;
            var manifest = new PiPackageManifest(
                ReadPaths(pi, "extensions", packageRoot, diagnostics, manifestPath),
                ReadPaths(pi, "skills", packageRoot, diagnostics, manifestPath),
                ReadPaths(pi, "prompts", packageRoot, diagnostics, manifestPath));
            var name = document.RootElement.TryGetProperty("name", out var nameValue) &&
                       nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString() ?? Path.GetFileName(Path.GetDirectoryName(manifestPath))
                : Path.GetFileName(Path.GetDirectoryName(manifestPath));
            return new PiPackageDefinition(name!, Path.GetDirectoryName(manifestPath)!, manifest);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new SkillDiagnostic(exception.Message, manifestPath));
            return null;
        }
        catch (IOException exception)
        {
            diagnostics.Add(new SkillDiagnostic(exception.Message, manifestPath));
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new SkillDiagnostic(exception.Message, manifestPath));
            return null;
        }
    }

    private static IReadOnlyList<string> ReadPaths(
        JsonElement manifest,
        string propertyName,
        string packageRoot,
        ICollection<SkillDiagnostic> diagnostics,
        string manifestPath)
    {
        if (!manifest.TryGetProperty(propertyName, out var paths) || paths.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return paths.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Where(path => IsSafePackagePath(path, packageRoot, diagnostics, manifestPath))
            .ToArray();
    }

    private static bool IsSafePackagePath(
        string path,
        string packageRoot,
        ICollection<SkillDiagnostic> diagnostics,
        string manifestPath)
    {
        if (Path.IsPathRooted(path))
        {
            diagnostics.Add(new SkillDiagnostic($"package resource path must be relative: {path}", manifestPath));
            return false;
        }

        var resolvedRoot = Path.GetFullPath(packageRoot);
        var resolvedPath = Path.GetFullPath(Path.Combine(resolvedRoot, path));
        var prefix = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;
        if (resolvedPath.StartsWith(prefix, StringComparison.Ordinal) ||
            string.Equals(resolvedPath, resolvedRoot, StringComparison.Ordinal))
        {
            return true;
        }

        diagnostics.Add(new SkillDiagnostic($"package resource path escapes package root: {path}", manifestPath));
        return false;
    }
}
