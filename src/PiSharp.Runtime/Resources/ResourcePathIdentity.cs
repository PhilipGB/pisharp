namespace PiSharp.Runtime.Resources;

internal static class ResourcePathIdentity
{
    internal static string Canonicalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try { return Resolve(fullPath, Directory.Exists(fullPath)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { return fullPath; }
    }

    private static string Resolve(string path, bool directory)
    {
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            return Resolve(target.FullName, target is DirectoryInfo);
        var parent = Path.GetDirectoryName(path);
        return parent is null || parent == path ? path : Path.Combine(Resolve(parent, directory: true), Path.GetFileName(path));
    }
}
