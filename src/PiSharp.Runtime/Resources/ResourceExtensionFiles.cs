namespace PiSharp.Runtime.Resources;

/// <summary>Discovers .NET extension entry points using Pi's explicit-entry and visibility rules.</summary>
internal static class ResourceExtensionFiles
{
    internal static IReadOnlyList<string> Enumerate(string root)
    {
        var ancestors = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!Directory.Exists(root)) return [];
        try
        {
            if (DeclaredEntries(root) is { } declared) return declared;
            var result = new List<string>();
            var ignore = new ResourceIgnoreRules(root);
            ignore.AddDirectory(root);
            foreach (var path in Directory.GetFileSystemEntries(root).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (name.StartsWith('.') || name == "node_modules") continue;
                var directory = Directory.Exists(path);
                if (ignore.IsIgnored(Path.GetRelativePath(root, path), directory)) continue;
                if (directory)
                {
                    if (DeclaredEntries(path) is { } entries) result.AddRange(entries);
                }
                else if (name.EndsWith(".dll", StringComparison.Ordinal) && ResourceFileDiscovery.IsRegularFile(path))
                    result.Add(Path.GetFullPath(path));
            }
            return result.Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }

        IReadOnlyList<string>? DeclaredEntries(string directory)
        {
            var identity = ResourcePathIdentity.Canonicalize(directory);
            if (!ancestors.Add(identity)) return null;
            try
            {
                var manifest = ResourcePackageManifest.Read(directory);
                if (manifest is not null && manifest.TryGetValue("extensions", out var declarations) && declarations.Count > 0)
                {
                    var selected = new List<string>();
                    foreach (var declaration in declarations)
                    {
                        var path = Path.GetFullPath(declaration, directory);
                        if (ResourceFileDiscovery.IsRegularFile(path)) selected.Add(path);
                        else if (Directory.Exists(path) && DeclaredEntries(path) is { } entries) selected.AddRange(entries);
                    }
                    if (selected.Count > 0) return selected.Distinct(StringComparer.Ordinal).ToArray();
                }
                var index = Path.Combine(directory, "index.dll");
                return ResourceFileDiscovery.IsRegularFile(index) ? [Path.GetFullPath(index)] : null;
            }
            finally { ancestors.Remove(identity); }
        }
    }
}
