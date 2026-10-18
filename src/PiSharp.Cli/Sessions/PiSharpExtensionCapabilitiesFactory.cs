using PiSharp.Cli.Composition;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal static class PiSharpExtensionCapabilitiesFactory
{
    public static PiSharpExtensionCapabilities Create(ActiveProjectRuntime projectBinding,
        ActiveModelRuntime modelBinding, ConversationRun run, string? sourcePath,
        PiSharpExtensionSessionContext? session,
        PiSharpExtensionCommandControlOperations? controls = null,
        Func<string, CancellationToken, Task>? sendUserMessageAsync = null,
        Func<PiSharpCustomMessage, bool, PiSharpMessageDeliveryMode, CancellationToken, Task>? sendCustomMessageAsync = null,
        Func<IReadOnlyList<PiSharpExtensionCommandInfo>>? getCommands = null,
        PiSharpExtensionSettingsFlagContext? settingsAndFlags = null)
    {
        ArgumentNullException.ThrowIfNull(projectBinding);
        ArgumentNullException.ThrowIfNull(modelBinding);
        ArgumentNullException.ThrowIfNull(run);

        var effectiveSettingsAndFlags = settingsAndFlags ?? (string.IsNullOrWhiteSpace(sourcePath)
            ? null
            : PiSharpExtensionSettingsFlagContextFactory.Create(projectBinding, sourcePath));
        return new PiSharpExtensionCapabilities(controls, session, effectiveSettingsAndFlags,
            () =>
            {
                var window = run.ContextWindowTokens;
                if (window is not > 0) return null;
                var tokens = run.EstimatedContextTokens;
                double? percent = tokens is null ? null : Math.Clamp((double)tokens.Value / window.Value * 100, 0, 100);
                return new PiSharpExtensionContextUsage(tokens, window.Value, percent);
            },
            () => modelBinding.Agent.SystemInstructions,
            sendUserMessageAsync,
            sendCustomMessageAsync,
            () =>
            {
                var selection = modelBinding.Selection;
                return new PiSharpExtensionModelInfo(selection.Provider.Id, selection.Model.Id,
                    selection.Model.Name ?? selection.Model.Id, selection.Model.ContextLength,
                    selection.Model.Reasoning == true);
            },
            () => ToPublicThinkingLevel(modelBinding.Thinking),
            () => run.ActiveToolNames,
            () =>
            {
                var sources = projectBinding.Runtime.Extensions.Registration.ToolSourceInfo;
                return Array.AsReadOnly(run.GetConfiguredTools().Select(tool =>
                {
                    if (sources.TryGetValue(tool.Name, out var source))
                        return tool with { Source = "extension", SourcePath = source.Path };
                    if (tool.Namespace?.StartsWith("mcp__", StringComparison.Ordinal) == true)
                        return tool with { Source = "mcp" };
                    return tool;
                }).ToArray());
            },
            getCommands,
            () => run.CanCompactFromExtensionCallback);
    }

    private static PiSharpThinkingLevel ToPublicThinkingLevel(string level) => level switch
    {
        "off" => PiSharpThinkingLevel.Off,
        "minimal" => PiSharpThinkingLevel.Minimal,
        "low" => PiSharpThinkingLevel.Low,
        "medium" => PiSharpThinkingLevel.Medium,
        "high" => PiSharpThinkingLevel.High,
        "xhigh" => PiSharpThinkingLevel.ExtraHigh,
        "max" => PiSharpThinkingLevel.Max,
        _ => PiSharpThinkingLevel.Medium
    };
}
