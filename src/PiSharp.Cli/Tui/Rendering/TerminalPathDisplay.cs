namespace PiSharp.Cli.Tui;

internal static class TerminalPathDisplay
{
    public static string FormatForFooter(string workingDirectory, string? homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (string.IsNullOrWhiteSpace(homeDirectory)) return workingDirectory;

        var resolvedWorkingDirectory = Path.GetFullPath(workingDirectory);
        var resolvedHomeDirectory = Path.GetFullPath(homeDirectory);
        var relativePath = Path.GetRelativePath(resolvedHomeDirectory, resolvedWorkingDirectory);
        if (relativePath == ".") return "~";

        var parentPrefix = ".." + Path.DirectorySeparatorChar;
        var alternateParentPrefix = ".." + Path.AltDirectorySeparatorChar;
        if (relativePath == ".." || relativePath.StartsWith(parentPrefix, StringComparison.Ordinal) ||
            relativePath.StartsWith(alternateParentPrefix, StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
            return workingDirectory;

        return "~" + Path.DirectorySeparatorChar + relativePath;
    }
}
