using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Tui;

internal static class TerminalStartupDetails
{
    public static IReadOnlyList<TerminalStartupDetail> Build(IReadOnlyList<string> contextFiles,
        ResourceCatalog resources, ExtensionCatalog extensions,
        string workingDirectory, IReadOnlyList<TerminalThemeDiagnostic>? themeDiagnostics = null)
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
        if (BuildDiagnosticDetail("Skill conflicts", resources.SkillDiagnostics, workingDirectory) is { } skillDiagnostics)
            details.Add(skillDiagnostics);
        if (BuildDiagnosticDetail("Prompt conflicts", resources.PromptDiagnostics, workingDirectory) is { } promptDiagnostics)
            details.Add(promptDiagnostics);
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
        if (themeDiagnostics is { Count: > 0 })
        {
            var lines = themeDiagnostics.SelectMany(item => new[]
            {
                new TerminalStartupDiagnosticLine(item.Path, 2, "warning"),
                new TerminalStartupDiagnosticLine(item.Message, 4, "warning")
            }).ToArray();
            var items = lines.Select(line => line.Text).ToArray();
            details.Add(new("Theme conflicts", items, items, ShowWhenQuiet: true,
                ShowEachItemWhenQuiet: true, DiagnosticLines: lines, HeaderStyle: "warning"));
        }
        return details;
    }

    private static TerminalStartupDetail? BuildDiagnosticDetail(string name,
        IReadOnlyList<ResourceDiagnostic> diagnostics, string workingDirectory)
    {
        if (diagnostics.Count == 0) return null;
        var lines = new List<TerminalStartupDiagnosticLine>();
        var collisions = diagnostics.Where(item => item.Collision is not null)
            .GroupBy(item => item.Collision!.Name, StringComparer.Ordinal);
        foreach (var group in collisions)
        {
            var grouped = group.ToArray();
            var first = grouped[0].Collision!;
            lines.Add(new($"\"{group.Key}\" collision:", 2, "warning"));
            lines.Add(new(DisplayResourcePath(first.WinnerPath, first.WinnerSourceInfo, workingDirectory),
                4, "dim", "✓", "success"));
            foreach (var item in grouped)
            {
                var collision = item.Collision!;
                // Pi has source metadata for selected resources, but renders duplicate losers as bare paths.
                lines.Add(new(DisplayResourcePath(collision.LoserPath, null, workingDirectory) + " (skipped)",
                    4, "dim", "✗", "warning"));
            }
        }

        foreach (var item in diagnostics.Where(item => item.Collision is null))
        {
            var style = item.Type == "error" ? "error" : "warning";
            if (item.Path is { } path)
            {
                lines.Add(new(DisplayResourcePath(path, item.SourceInfo, workingDirectory), 2, style));
                lines.Add(new(item.Message, 4, style));
            }
            else lines.Add(new(item.Message, 2, style));
        }

        var items = lines.Select(line => line.Text).ToArray();
        return new(name, items, items, ShowWhenQuiet: true, ShowEachItemWhenQuiet: true,
            DiagnosticLines: lines, HeaderStyle: "warning");
    }

    private static string DisplayResourcePath(string path, ResourceSourceInfo? sourceInfo,
        string workingDirectory)
    {
        var displayPath = DisplayPath(path, workingDirectory);
        if (sourceInfo is null) return displayPath;
        var source = sourceInfo.Source;
        var scope = sourceInfo.Scope;
        if (source == "local")
        {
            return scope switch
            {
                "user" => $"user {displayPath}",
                "project" => $"project {displayPath}",
                "temporary" => $"path (temp) {displayPath}",
                _ => $"path {displayPath}"
            };
        }

        if (source == "cli") return scope == "temporary"
            ? $"path (temp) {displayPath}" : $"path {displayPath}";

        var scopeLabel = scope switch
        {
            "user" => "user",
            "project" => "project",
            "temporary" => "temp",
            _ => null
        };
        return scopeLabel is null ? $"{source} {displayPath}" : $"{source} ({scopeLabel}) {displayPath}";
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
