namespace PiSharp.Runtime.Resources;

internal enum ResourceDiscoveryKind { Skills, AgentSkills, Prompts, ConfiguredPrompts, AutoPrompts }

/// <summary>Finds resource files using the selected directory's discovery rules.</summary>
internal static class ResourceFileDiscovery
{
    internal static IReadOnlyList<string> Enumerate(string path, ResourceDiscoveryKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRegularFile(path))
            return path.EndsWith(".md", StringComparison.Ordinal) ? [Path.GetFullPath(path)] : [];
        if (!Directory.Exists(path)) return [];

        var files = new List<string>();
        var skills = kind is ResourceDiscoveryKind.Skills or ResourceDiscoveryKind.AgentSkills;
        if (skills)
        {
            var ignore = new ResourceIgnoreRules(Path.GetFullPath(path));
            var ancestors = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            VisitSkills(Path.GetFullPath(path), includeAliases: true);

            void VisitSkills(string directory, bool includeAliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = ResourcePathIdentity.Canonicalize(directory);
                if (!ancestors.Add(identity)) return;
                try
                {
                    ignore.AddDirectory(directory);
                    var entries = ReadEntries(directory);
                    var declared = entries.FirstOrDefault(entry => Path.GetFileName(entry) == "SKILL.md" && IsRegularFile(entry) &&
                        !ignore.IsIgnored(Path.GetRelativePath(path, entry), directory: false));
                    if (declared is not null)
                    {
                        files.Add(declared);
                        return;
                    }
                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(entry);
                        if (name.StartsWith(".", StringComparison.Ordinal) || name == "node_modules") continue;
                        var isDirectory = Directory.Exists(entry);
                        if (ignore.IsIgnored(Path.GetRelativePath(path, entry), isDirectory)) continue;
                        if (isDirectory) VisitSkills(entry, includeAliases: false);
                        else if ((kind == ResourceDiscoveryKind.AgentSkills ? !includeAliases : includeAliases) && name.EndsWith(".md", StringComparison.Ordinal) && IsRegularFile(entry))
                            files.Add(entry);
                    }
                }
                finally { ancestors.Remove(identity); }
            }
        }
        else
        {
            var ignore = kind == ResourceDiscoveryKind.Prompts ? null : new ResourceIgnoreRules(Path.GetFullPath(path));
            var ancestors = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            VisitPrompts(Path.GetFullPath(path));

            void VisitPrompts(string directory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = ResourcePathIdentity.Canonicalize(directory);
                if (!ancestors.Add(identity)) return;
                try
                {
                    ignore?.AddDirectory(directory);
                    foreach (var entry in ReadEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(entry);
                        if (ignore is not null && (name.StartsWith(".", StringComparison.Ordinal) || name == "node_modules")) continue;
                        var isDirectory = Directory.Exists(entry);
                        if (ignore?.IsIgnored(Path.GetRelativePath(path, entry), isDirectory) == true) continue;
                        if (isDirectory && kind == ResourceDiscoveryKind.ConfiguredPrompts) VisitPrompts(entry);
                        else if (entry.EndsWith(".md", StringComparison.Ordinal) && IsRegularFile(entry)) files.Add(entry);
                    }
                }
                finally { ancestors.Remove(identity); }
            }
        }
        return files;
    }

    private static string[] ReadEntries(string directory)
    {
        try { return Directory.GetFileSystemEntries(directory).Order(Comparer<string>.Create(CompareEntries)).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static int CompareEntries(string left, string right)
    {
        var first = left.EnumerateRunes().GetEnumerator();
        var second = right.EnumerateRunes().GetEnumerator();
        while (first.MoveNext())
        {
            if (!second.MoveNext()) return 1;
            var comparison = first.Current.Value.CompareTo(second.Current.Value);
            if (comparison != 0) return comparison;
        }
        return second.MoveNext() ? -1 : 0;
    }

    private static bool IsRegularFile(string path)
    {
        try { return LocalResourceFileType.IsRegularFile(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
