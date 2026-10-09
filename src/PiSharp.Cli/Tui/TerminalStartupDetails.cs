using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Tui;

internal static class TerminalStartupDetails
{
    public static IReadOnlyList<TerminalStartupDetail> Build(IReadOnlyList<string> contextFiles,
        ResourceCatalog resources, ExtensionCatalog extensions,
        string workingDirectory)
    {
        var details = new List<TerminalStartupDetail>();
        if (contextFiles.Count > 0)
            details.Add(new("Context", contextFiles.Select(path => DisplayContextPath(path, workingDirectory)).ToArray(),
                contextFiles.Select(path => DisplayPath(path, workingDirectory)).ToArray()));
        if (resources.Skills.Count > 0)
            details.Add(new("Skills", resources.Skills.Select(item => item.Name)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                resources.Skills.Select(item => DisplayPath(item.Path, workingDirectory))
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        if (resources.Prompts.Count > 0)
            details.Add(new("Prompts", resources.Prompts.Select(item => "/" + item.Name)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                resources.Prompts.Select(item => "/" + item.Name)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        var extensionItems = extensions.LoadedBuiltins.Order(StringComparer.Ordinal)
            .Select(name => "builtin:" + name)
            .Concat(extensions.LoadedExtensions.Select(item => DisplayPath(item.Path, workingDirectory)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (extensionItems.Length > 0)
            details.Add(new("Extensions", extensionItems));
        return details;
    }

    private static string DisplayContextPath(string path, string workingDirectory)
    {
        var fullPath = Path.GetFullPath(path, workingDirectory);
        var relative = Path.GetRelativePath(workingDirectory, fullPath);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? relative : AbbreviateHome(fullPath);
    }

    private static string DisplayPath(string path, string workingDirectory) =>
        AbbreviateHome(Path.GetFullPath(path, workingDirectory));

    private static string AbbreviateHome(string fullPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return fullPath;
        var prefix = home.EndsWith(Path.DirectorySeparatorChar) ? home : home + Path.DirectorySeparatorChar;
        return fullPath.Equals(home, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? "~"
            : fullPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? "~" + fullPath[prefix.Length..]
                : fullPath;
    }
}
