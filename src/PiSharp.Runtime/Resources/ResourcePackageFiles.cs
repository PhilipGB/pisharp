namespace PiSharp.Runtime.Resources;

internal sealed record PackageResourceFile(string Path, ResourceSourceInfo Source, bool Enabled);

internal sealed class ResourcePackageFiles
{
    internal List<PackageResourceFile> Skills { get; } = [];
    internal List<PackageResourceFile> Prompts { get; } = [];
    internal List<PackageResourceFile> Themes { get; } = [];
    internal List<PackageResourceFile> Extensions { get; } = [];

    internal static ResourcePackageFiles Load(string cwd, string agentDirectory, bool trusted,
        IReadOnlyList<ResourcePackageSource>? user, IReadOnlyList<ResourcePackageSource>? project, CancellationToken token)
    {
        var result = new ResourcePackageFiles();
        var selected = new Dictionary<string, (ResourcePackageSource Package, string Scope)>(StringComparer.Ordinal);
        var declarations = (trusted ? project ?? [] : []).Select(package => (Package: package, Scope: "project"))
            .Concat((user ?? []).Select(package => (Package: package, Scope: "user")));
        var skillPaths = new HashSet<string>(StringComparer.Ordinal);
        var promptPaths = new HashSet<string>(StringComparer.Ordinal);
        var themePaths = new HashSet<string>(StringComparer.Ordinal);
        var extensionPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            token.ThrowIfCancellationRequested();
            var package = declaration.Package;
            if (package.Source.StartsWith("npm:", StringComparison.Ordinal) ||
                package.Source.StartsWith("git:", StringComparison.Ordinal)) continue;
            var baseDirectory = declaration.Scope == "project" ? Path.Combine(cwd, ".pi") : agentDirectory;
            var root = ResourcePackagePath.Resolve(package.Source, baseDirectory);
            if (selected.TryGetValue(root, out var prior) &&
                !(prior.Scope == "project" && !prior.Package.Autoload && declaration.Scope == "user")) continue;
            selected.TryAdd(root, declaration);
            if (ResourceFileDiscovery.IsRegularFile(root))
            {
                if (extensionPaths.Add(root)) result.Extensions.Add(new(root,
                    new(root, package.Source, declaration.Scope, "package", Path.GetDirectoryName(root)!), true));
                continue;
            }
            if (!Directory.Exists(root)) continue;
            var manifest = ResourcePackageManifest.Read(root);
            if (manifest is null && !package.HasFilter &&
                !new[] { "extensions", "skills", "prompts", "themes" }.Any(kind => Directory.Exists(Path.Combine(root, kind))))
            {
                foreach (var path in ResourceExtensionFiles.Enumerate(root))
                    if (extensionPaths.Add(path)) result.Extensions.Add(new(path,
                        new(path, package.Source, declaration.Scope, "package", root), true));
            }
            Add("skills", package.Skills, result.Skills, skillPaths);
            Add("prompts", package.Prompts, result.Prompts, promptPaths);
            Add("themes", package.Themes, result.Themes, themePaths);
            Add("extensions", package.Extensions, result.Extensions, extensionPaths);

            void Add(string kind, IReadOnlyList<string>? filter, List<PackageResourceFile> target, HashSet<string> seen)
            {
                IReadOnlyList<string>? entries = null;
                var declared = manifest is not null && manifest.TryGetValue(kind, out entries);
                var useConvention = manifest is null;
                if (package.HasFilter)
                    useConvention = filter is not null ? entries is not { Count: > 0 } : !declared;
                var candidates = useConvention
                    ? Collect(Path.Combine(root, kind), kind, token)
                    : Entries(entries ?? [], root, kind, token);
                var allowed = useConvention ? candidates : Apply(candidates, (entries ?? []).Where(LocalResourcePathRules.IsPattern).ToArray(), root);
                var enabled = filter is null ? allowed : filter.Count == 0 ? [] : Apply(allowed, filter, root);
                var delta = package.HasFilter && !package.Autoload;
                foreach (var path in allowed)
                {
                    if (delta && !HasDelta(path, filter ?? [], root)) continue;
                    if (!seen.Add(path)) continue;
                    var active = delta ? DeltaEnabled(path, filter ?? [], root) : enabled.Contains(path, StringComparer.Ordinal);
                    target.Add(new(path, new(path, package.Source, declaration.Scope, "package", root), active));
                }
            }
        }
        RetainCanonicalEntries(result.Skills);
        RetainCanonicalEntries(result.Prompts);
        RetainCanonicalEntries(result.Themes);
        RetainCanonicalEntries(result.Extensions);
        return result;
    }

    private static IReadOnlyList<string> Entries(IReadOnlyList<string> entries, string root, string kind, CancellationToken token)
    {
        var result = new List<string>();
        foreach (var entry in entries.Where(entry => !LocalResourcePathRules.IsPattern(entry)))
        {
            var path = Path.GetFullPath(entry, root);
            if (entry.IndexOfAny(['*', '?']) < 0) result.AddRange(Collect(path, kind, token));
            else
            {
                var first = path.IndexOfAny(['*', '?']);
                var separator = path.LastIndexOf(Path.DirectorySeparatorChar, first);
                var searchRoot = separator == 0 ? Path.DirectorySeparatorChar.ToString() : path[..separator];
                foreach (var match in VisiblePaths(searchRoot, token).Where(candidate =>
                             Path.GetRelativePath(root, candidate).Split(Path.DirectorySeparatorChar)
                                 .All(segment => segment == ".." || !segment.StartsWith('.')) &&
                             LocalResourcePathRules.GlobMatch(candidate.Replace('\\', '/'), path.Replace('\\', '/')))
                             .Order(StringComparer.Ordinal))
                    result.AddRange(Collect(match, kind, token));
            }
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> Collect(string path, string kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (kind == "extensions")
        {
            try
            {
                if (File.Exists(path)) return Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? [Path.GetFullPath(path)] : [];
                return ResourceExtensionFiles.Enumerate(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        }
        return ResourceFileDiscovery.Enumerate(path, kind switch
        {
            "skills" => ResourceDiscoveryKind.Skills,
            "themes" => ResourceDiscoveryKind.ConfiguredThemes,
            _ => ResourceDiscoveryKind.ConfiguredPrompts
        }, token);
    }

    private static void RetainCanonicalEntries(List<PackageResourceFile> files)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        files.RemoveAll(file => !seen.Add(ResourcePathIdentity.Canonicalize(file.Path)));
    }

    private static IEnumerable<string> VisiblePaths(string root, CancellationToken token)
    {
        var ancestors = new HashSet<string>(StringComparer.Ordinal);
        return Visit(root);

        IEnumerable<string> Visit(string directory)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory) || !ancestors.Add(ResourcePathIdentity.Canonicalize(directory))) yield break;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ancestors.Remove(ResourcePathIdentity.Canonicalize(directory)); yield break; }
            foreach (var entry in entries)
            {
                if (Path.GetFileName(entry).StartsWith('.')) continue;
                yield return entry;
                if (Directory.Exists(entry)) foreach (var child in Visit(entry)) yield return child;
            }
            ancestors.Remove(ResourcePathIdentity.Canonicalize(directory));
        }
    }

    private static IReadOnlyList<string> Apply(IReadOnlyList<string> paths, IReadOnlyList<string> patterns, string root)
    {
        var includes = patterns.Where(pattern => !LocalResourcePathRules.IsPattern(pattern)).ToArray();
        return paths.Where(path =>
        {
            var included = includes.Length == 0 || includes.Any(pattern => Glob(path, pattern, root)) ||
                patterns.Where(pattern => pattern.StartsWith('+')).Any(pattern => Exact(path, pattern[1..], root));
            return included && LocalResourcePathRules.IsEnabledByOverrides(path, patterns, root, skillPath: true);
        }).ToArray();
    }

    private static bool HasDelta(string path, IReadOnlyList<string> patterns, string root) => patterns.Any(pattern =>
        pattern.StartsWith('+') || pattern.StartsWith('-') ? Exact(path, pattern[1..], root) :
        Glob(path, pattern.StartsWith('!') ? pattern[1..] : pattern, root));

    private static bool DeltaEnabled(string path, IReadOnlyList<string> patterns, string root)
    {
        var included = patterns.Any(pattern => !pattern.StartsWith('!') && !pattern.StartsWith('-') &&
            (pattern.StartsWith('+') ? Exact(path, pattern[1..], root) : Glob(path, pattern, root)));
        return included && LocalResourcePathRules.IsEnabledByOverrides(path, patterns, root, skillPath: true);
    }

    private static bool Glob(string path, string pattern, string root) =>
        !LocalResourcePathRules.IsEnabledByOverrides(path, ["!" + pattern], root, skillPath: true);

    private static bool Exact(string path, string pattern, string root) =>
        !LocalResourcePathRules.IsEnabledByOverrides(path, ["-" + pattern], root, skillPath: true);
}
