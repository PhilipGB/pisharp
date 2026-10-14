using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Resources;

internal sealed record ResourceFileSelection(string Path, ResourceSourceInfo Source, ResourceDiagnostic? Diagnostic = null);

/// <summary>Selects canonical roots and source metadata before file validation and name resolution.</summary>
internal sealed class ResourcePathSelection(string cwd, string agentDirectory, bool trusted, CancellationToken token,
    ResourcePackageFiles packages)
{
    internal List<ResourceFileSelection> SkillFiles { get; } = [];
    internal List<ResourceFileSelection> PromptFiles { get; } = [];
    internal List<ResourceDiagnostic> PromptPathErrors { get; } = [];
    private readonly Dictionary<string, ResourceSourceInfo> _metadata = new(PathComparer);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static ResourcePathSelection Create(string cwd, string agentDirectory, bool trusted, CancellationToken token,
        bool discoverSkills, bool discoverPrompts, IReadOnlyList<string>? additionalSkills, IReadOnlyList<string>? additionalPrompts,
        IReadOnlyList<string>? userSkills, IReadOnlyList<string>? projectSkills,
        IReadOnlyList<string>? userPrompts, IReadOnlyList<string>? projectPrompts, ExtensionResourceDiscovery? extensions,
        IReadOnlyList<ResourcePackageSource>? userPackages, IReadOnlyList<ResourcePackageSource>? projectPackages)
    {
        var packages = ResourcePackageFiles.Load(cwd, agentDirectory, trusted, userPackages, projectPackages, token);
        var result = new ResourcePathSelection(cwd, agentDirectory, trusted, token, packages);
        var skills = result.Defaults(true, userSkills, projectSkills);
        var prompts = result.Defaults(false, userPrompts, projectPrompts);
        result.Select(true, discoverSkills ? skills : [], additionalSkills, extensions?.SkillPaths);
        result.Select(false, discoverPrompts ? prompts : [], additionalPrompts, extensions?.PromptPaths);
        return result;
    }

    private List<string> Defaults(bool skills, IReadOnlyList<string>? userEntries, IReadOnlyList<string>? projectEntries)
    {
        var selected = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        var kind = skills ? "skills" : "prompts";
        var packagePaths = skills ? packages.Skills : packages.Prompts;
        foreach (var file in packagePaths) _metadata.TryAdd(file.Path, file.Source);
        if (trusted)
        {
            Configured(projectEntries, Path.Combine(cwd, ".pi"), "project");
            Automatic(Path.Combine(cwd, ".pi", kind), projectEntries, Path.Combine(cwd, ".pi"), "project");
            if (skills)
            {
                var homeSkills = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
                foreach (var directory in AncestorAgentsDirectories())
                    if (!PathComparer.Equals(directory, homeSkills))
                        Automatic(directory, projectEntries, Path.GetDirectoryName(directory)!, "project", agents: true);
            }
        }
        Configured(userEntries, agentDirectory, "user");
        Automatic(Path.Combine(agentDirectory, kind), userEntries, agentDirectory, "user");
        if (skills)
        {
            var homeAgents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents");
            Automatic(Path.Combine(homeAgents, "skills"), userEntries, homeAgents, "user", agents: true);
        }
        selected.AddRange(packagePaths.Where(file => file.Enabled).Select(file => file.Path));
        return selected;

        void Configured(IReadOnlyList<string>? entries, string baseDirectory, string scope)
        {
            var files = LocalResourcePathRules.GetPaths(entries).SelectMany(entry =>
            {
                var path = LocalResourcePathRules.ResolvePath(entry, baseDirectory);
                try { if (LocalResourceFileType.IsRegularFile(path)) return new[] { path }; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
                return ResourceFileDiscovery.Enumerate(path, skills ? ResourceDiscoveryKind.Skills : ResourceDiscoveryKind.ConfiguredPrompts, token);
            });
            Add(files, entries, baseDirectory, "local", scope, sourceBase: null);
        }

        void Automatic(string directory, IReadOnlyList<string>? entries, string baseDirectory, string scope, bool agents = false)
        {
            var mode = skills ? agents ? ResourceDiscoveryKind.AgentSkills : ResourceDiscoveryKind.Skills : ResourceDiscoveryKind.AutoPrompts;
            Add(ResourceFileDiscovery.Enumerate(directory, mode, token), entries, baseDirectory, "auto", scope, baseDirectory);
        }

        void Add(IEnumerable<string> files, IReadOnlyList<string>? entries, string baseDirectory, string source, string scope, string? sourceBase)
        {
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(path);
                if (packagePaths.Any(file => PathComparer.Equals(file.Path, full))) continue;
                if (!seen.Add(full)) continue;
                _metadata.TryAdd(full, new(full, source, scope, "top-level", sourceBase is null ? null : Path.GetFullPath(sourceBase)));
                if (LocalResourcePathRules.IsEnabledByOverrides(full, entries, baseDirectory, skills)) selected.Add(full);
            }
        }
    }

    private void Select(bool skills, IReadOnlyList<string> defaults, IReadOnlyList<string>? additional,
        IReadOnlyList<ExtensionDiscoveredResourcePath>? extensionPaths)
    {
        var roots = new HashSet<string>(PathComparer);
        var additionalPaths = (additional ?? []).Select(p => LocalResourcePathRules.ResolvePath(p, cwd)).ToArray();
        foreach (var path in defaults.Concat(additionalPaths)
            .Concat((extensionPaths ?? []).Select(p => Path.GetFullPath(p.Path))))
        {
            token.ThrowIfCancellationRequested();
            if (!roots.Add(ResourcePathIdentity.Canonicalize(path))) continue;
            if (!LocalResourceFileType.ExistsFollowingLinks(path))
            {
                if (skills) SkillFiles.Add(new(path, Source(path, path, true, extensionPaths),
                    new("warning", "skill path does not exist", path)));
                else if (additionalPaths.Contains(path, PathComparer))
                    PromptPathErrors.Add(new("error", "Prompt template path does not exist", path));
                continue;
            }
            var directory = Directory.Exists(path);
            if (!directory && (!LocalResourceFileType.IsRegularFile(path) || !path.EndsWith(".md", StringComparison.Ordinal)))
            {
                if (skills) SkillFiles.Add(new(path, Source(path, path, true, extensionPaths),
                    new("warning", "skill path is not a markdown file", path)));
                continue;
            }
            var files = directory ? ResourceFileDiscovery.Enumerate(path,
                skills ? ResourceDiscoveryKind.Skills : ResourceDiscoveryKind.Prompts, token) : new[] { path };
            foreach (var file in files)
                (skills ? SkillFiles : PromptFiles).Add(new(file, Source(file, path, skills, extensionPaths)));
        }
    }

    private ResourceSourceInfo Source(string file, string root, bool skills, IReadOnlyList<ExtensionDiscoveredResourcePath>? extras)
    {
        foreach (var extra in extras ?? [])
            if (IsUnder(file, extra.Path)) return extra.SourceInfo with { Path = file };
        if (_metadata.TryGetValue(file, out var source)) return source with { Path = file };
        foreach (var metadata in _metadata)
            if (IsUnder(file, metadata.Key)) return metadata.Value with { Path = file };
        var kind = skills ? "skills" : "prompts";
        var userRoot = Path.Combine(agentDirectory, kind);
        var projectRoot = Path.Combine(cwd, ".pi", kind);
        var selected = skills ? root : file;
        var scope = IsUnder(selected, userRoot) ? "user" : IsUnder(selected, projectRoot) ? "project" : "temporary";
        var baseDir = !skills && scope != "temporary" ? scope == "user" ? userRoot : projectRoot : Path.GetDirectoryName(file);
        return new(file, "local", scope, "top-level", baseDir);
    }

    private IEnumerable<string> AncestorAgentsDirectories()
    {
        var boundary = Path.GetFullPath(cwd);
        while (!Directory.Exists(Path.Combine(boundary, ".git")) && !File.Exists(Path.Combine(boundary, ".git")))
        {
            var parent = Path.GetDirectoryName(boundary);
            if (parent is null) { boundary = ""; break; }
            boundary = parent;
        }
        var directory = Path.GetFullPath(cwd);
        while (true)
        {
            yield return Path.Combine(directory, ".agents", "skills");
            if (PathComparer.Equals(directory, boundary)) yield break;
            var parent = Path.GetDirectoryName(directory);
            if (parent is null) yield break;
            directory = parent;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
