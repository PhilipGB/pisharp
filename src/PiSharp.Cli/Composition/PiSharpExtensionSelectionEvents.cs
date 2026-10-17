using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Composition;

internal static class PiSharpExtensionSelectionEvents
{
    public static async ValueTask PublishChangesAsync(PiAgent agent, ModelSelection previousSelection,
        string previousThinking, ModelSelection currentSelection, string currentThinking,
        PiSharpExtensionModelSelectionSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(previousSelection);
        ArgumentNullException.ThrowIfNull(currentSelection);

        if (!string.Equals(previousThinking, currentThinking, StringComparison.Ordinal))
            await agent.ExtensionEvents.DispatchThinkingLevelSelectAsync(new(
                ToThinkingLevel(currentThinking), ToThinkingLevel(previousThinking)), cancellationToken).ConfigureAwait(false);

        if (!HasSameIdentity(previousSelection, currentSelection))
            await agent.ExtensionEvents.DispatchModelSelectAsync(new(
                ToModelInfo(currentSelection), ToModelInfo(previousSelection), source), cancellationToken).ConfigureAwait(false);
    }

    public static Task PublishRestoreAsync(PiAgent agent, ModelSelection previousSelection,
        ModelSelection currentSelection, CancellationToken cancellationToken)
        => PublishRestoreAsync(agent, previousSelection, "off", currentSelection, "off", cancellationToken);

    public static async Task PublishRestoreAsync(PiAgent agent, ModelSelection previousSelection,
        string previousThinking, ModelSelection currentSelection, string currentThinking,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(previousSelection);
        ArgumentNullException.ThrowIfNull(currentSelection);

        if (!string.Equals(previousThinking, currentThinking, StringComparison.Ordinal))
            await agent.ExtensionEvents.DispatchThinkingLevelSelectAsync(new(
                ToThinkingLevel(currentThinking), ToThinkingLevel(previousThinking)), cancellationToken).ConfigureAwait(false);

        if (!HasSameIdentity(previousSelection, currentSelection))
            await agent.ExtensionEvents.DispatchModelSelectAsync(new(
                ToModelInfo(currentSelection), ToModelInfo(previousSelection), PiSharpExtensionModelSelectionSource.Restore),
                cancellationToken).ConfigureAwait(false);
    }

    private static bool HasSameIdentity(ModelSelection left, ModelSelection right) =>
        string.Equals(left.Provider.Id, right.Provider.Id, StringComparison.Ordinal) &&
        string.Equals(left.Model.Id, right.Model.Id, StringComparison.Ordinal);

    private static PiSharpExtensionModelInfo ToModelInfo(ModelSelection selection) => new(
        selection.Provider.Id, selection.Model.Id, selection.Model.Name ?? selection.Model.Id,
        selection.Model.ContextLength, selection.Model.Reasoning == true);

    private static PiSharpThinkingLevel ToThinkingLevel(string level) => level.ToLowerInvariant() switch
    {
        "off" => PiSharpThinkingLevel.Off,
        "minimal" => PiSharpThinkingLevel.Minimal,
        "low" => PiSharpThinkingLevel.Low,
        "medium" => PiSharpThinkingLevel.Medium,
        "high" => PiSharpThinkingLevel.High,
        "xhigh" => PiSharpThinkingLevel.ExtraHigh,
        "max" => PiSharpThinkingLevel.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown thinking level.")
    };
}
