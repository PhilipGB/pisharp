namespace PiSharp.Runtime.Resources;

/// <summary>Finds skill roots and direct prompt files without opening nonregular entries.</summary>
internal static class ResourceFileDiscovery
{
    internal static IReadOnlyList<string> Enumerate(string path, bool skills, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsRegularFile(path))
            return path.EndsWith(".md", StringComparison.Ordinal) ? [Path.GetFullPath(path)] : [];
        if (!Directory.Exists(path)) return [];

        var files = new List<string>();
        if (skills)
        {
            var ignore = new ResourceIgnoreRules(Path.GetFullPath(path));
            var ancestors = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            VisitSkills(Path.GetFullPath(path), includeAliases: true);

            void VisitSkills(string directory, bool includeAliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string identity;
                try { identity = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
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
                        else if (includeAliases && name.EndsWith(".md", StringComparison.Ordinal) && IsRegularFile(entry))
                            files.Add(entry);
                    }
                }
                finally { ancestors.Remove(identity); }
            }
        }
        else
        {
            foreach (var entry in ReadEntries(Path.GetFullPath(path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EndsWith(".md", StringComparison.Ordinal) && IsRegularFile(entry)) files.Add(entry);
            }
        }
        return files;
    }

    private static string[] ReadEntries(string directory)
    {
        try { return Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsRegularFile(string path)
    {
        try { return LocalResourceFileType.IsRegularFile(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
