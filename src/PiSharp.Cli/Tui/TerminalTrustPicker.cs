using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Tui;

/// <summary>Edits persisted project trust decisions through the shared terminal selection overlay.</summary>
internal sealed class TerminalTrustPicker(TerminalEditor editor)
{
    private sealed record TrustChoice(bool Trusted, IReadOnlyList<ProjectTrustUpdate> Updates);

    public async Task<bool?> ShowAsync(ProjectTrust trustStore, string cwd, bool sessionTrusted,
        CancellationToken cancellationToken = default)
    {
        var projectPath = ProjectTrust.CanonicalizePath(cwd);
        var savedDecision = await trustStore.GetEntryAsync(projectPath, cancellationToken);
        var options = BuildOptions(projectPath, savedDecision);
        var selectedKey = options.FirstOrDefault(option => option.IsCurrent)?.Key;
        var selected = editor.ShowInlineSelectionList("\nProject trust", options,
            header:
            [
                projectPath,
                "",
                $"Saved decision: {FormatDecision(projectPath, savedDecision)}",
                $"Current session: {(sessionTrusted ? "trusted" : "untrusted")}",
                ""
            ],
            footer: "↑↓ navigate  enter save  escape/ctrl+c cancel",
            selectedKey: selectedKey,
            optionIndent: 1,
            bottomMargin: 2,
            bottomSpacerLines: 1,
            showCurrentMarker: true);
        if (selected is null) return null;

        await trustStore.SetManyAsync(selected.Option.Value.Updates, cancellationToken);
        return selected.Option.Value.Trusted;
    }

    private static IReadOnlyList<TerminalSelectionOption<TrustChoice>> BuildOptions(string projectPath,
        ProjectTrustEntry? savedDecision)
    {
        var options = new List<TerminalSelectionOption<TrustChoice>>
        {
            new("trust", new(true, [new(projectPath, true)]), "Trust",
                SearchText: projectPath,
                IsCurrent: IsSavedDecision(savedDecision, projectPath, true))
        };
        var parentPath = Path.GetDirectoryName(projectPath);
        if (parentPath is not null)
        {
            options.Add(new("parent", new(true,
                    [new(parentPath, true), new(projectPath, null)]),
                $"Trust parent folder ({parentPath})",
                SearchText: parentPath,
                IsCurrent: IsSavedDecision(savedDecision, parentPath, true)));
        }
        options.Add(new("deny", new(false, [new(projectPath, false)]), "Do not trust",
            SearchText: projectPath,
            IsCurrent: IsSavedDecision(savedDecision, projectPath, false)));
        return options;
    }

    private static bool IsSavedDecision(ProjectTrustEntry? savedDecision, string path, bool decision) =>
        savedDecision is not null && savedDecision.Decision == decision && PathsEqual(savedDecision.Path, path);

    private static string FormatDecision(string projectPath, ProjectTrustEntry? decision)
    {
        if (decision is null) return "none";
        var label = decision.Decision ? "trusted" : "untrusted";
        return PathsEqual(decision.Path, projectPath)
            ? $"{label} ({decision.Path})"
            : $"{label} (inherited from {decision.Path})";
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
}
