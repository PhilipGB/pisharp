namespace PiSharp.Runtime.Resources;

/// <summary>Resolves an existing directory path through symlinked path segments.</summary>
internal static class CanonicalDirectoryPath
{
    public static string Resolve(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (root is null) return fullPath;

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current)) continue;
            try
            {
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
            catch (IOException)
            {
                // Keep the normalized path when filesystem canonicalization is unavailable.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the normalized path when filesystem canonicalization is unavailable.
            }
        }

        return Path.GetFullPath(current);
    }
}
