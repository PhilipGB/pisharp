namespace PiSharp.Cli.Tui;

internal sealed record TerminalThinkingSelection(string Level, bool SetAsDefault);

/// <summary>Interactive selector for levels supported by the active model.</summary>
internal sealed class TerminalThinkingSelector(TerminalEditor editor)
{
    public async Task<TerminalThinkingSelection?> ShowAsync(ModelSelection selection, string currentLevel,
        string? defaultLevel, CancellationToken cancellationToken = default)
    {
        var levels = ThinkingLevels.AvailableForModel(selection.Model.Reasoning, selection.Model.ThinkingLevelMap);
        var options = CreateOptions(levels, currentLevel, defaultLevel);
        var selected = await editor.ShowSelectionListAsync("Thinking Level", options, currentLevel,
            actionHint: $"{editor.GetDisplayKeys("app.thinking.cycle")} cycles thinking levels in-session · " +
                $"{editor.GetDisplayKeys("app.thinking.save")} sets the selected level as default",
            cancellationToken: cancellationToken, saveAction: "app.thinking.save").ConfigureAwait(false);
        return selected is null ? null : new(selected.Option.Value, selected.SetAsDefault);
    }

    internal static IReadOnlyList<TerminalSelectionOption<string>> CreateOptions(
        IReadOnlyList<string> levels, string currentLevel, string? defaultLevel)
    {
        var effectiveDefault = defaultLevel ?? ThinkingLevels.Default;
        return levels.Select(level => new TerminalSelectionOption<string>(level, level, level,
            ThinkingLevels.Description(level) + (level.Equals(effectiveDefault, StringComparison.OrdinalIgnoreCase) ? " · default" : ""),
            IsCurrent: level.Equals(currentLevel, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
