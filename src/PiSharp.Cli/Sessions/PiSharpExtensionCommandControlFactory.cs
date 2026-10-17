using PiSharp.Cli.Composition;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal static class PiSharpExtensionCommandControlFactory
{
    public static PiSharpExtensionCommandControlOperations Create(
        Func<ConversationRun> getRun,
        Func<IReadOnlyList<PiSharpExtensionCommandInfo>> getCommands,
        Func<ExtensionRegistration?> getExtensions,
        ProviderModelRuntime modelRuntime,
        ActiveModelRuntime modelBinding,
        ConversationModelTransitions modelTransitions,
        Action abortCurrentRun,
        Func<CancellationToken, Task> cancelCurrentRunAsync,
        Action requestShutdown)
    {
        ArgumentNullException.ThrowIfNull(getRun);
        ArgumentNullException.ThrowIfNull(getCommands);
        ArgumentNullException.ThrowIfNull(getExtensions);
        ArgumentNullException.ThrowIfNull(modelRuntime);
        ArgumentNullException.ThrowIfNull(modelBinding);
        ArgumentNullException.ThrowIfNull(modelTransitions);
        ArgumentNullException.ThrowIfNull(abortCurrentRun);
        ArgumentNullException.ThrowIfNull(cancelCurrentRunAsync);
        ArgumentNullException.ThrowIfNull(requestShutdown);

        return new PiSharpExtensionCommandControlOperations(
            getCommands,
            () => getRun().ActiveToolNames,
            () =>
            {
                var sources = getExtensions()?.ToolSourceInfo ??
                    new Dictionary<string, PiSharp.Runtime.Resources.ResourceSourceInfo>(StringComparer.Ordinal);
                return Array.AsReadOnly(getRun().GetConfiguredTools().Select(tool =>
                {
                    if (sources.TryGetValue(tool.Name, out var source))
                        return tool with { Source = "extension", SourcePath = source.Path };
                    if (tool.Namespace?.StartsWith("mcp__", StringComparison.Ordinal) == true)
                        return tool with { Source = "mcp" };
                    return tool;
                }).ToArray());
            },
            () =>
            {
                var selection = modelBinding.Selection;
                return new PiSharpExtensionModelInfo(selection.Provider.Id, selection.Model.Id,
                    selection.Model.Name ?? selection.Model.Id, selection.Model.ContextLength,
                    selection.Model.Reasoning == true);
            },
            names => getRun().SetActiveTools(names),
            async (providerId, modelId, cancellationToken) =>
            {
                IReadOnlyList<PiSharp.Runtime.Providers.ModelDescriptor> models;
                try
                {
                    models = await modelRuntime.ListModelsAsync(providerId, cancellationToken,
                        includeOutOfScope: true).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    return new(PiSharpModelSelectionStatus.UnknownModel, "Unknown provider or model.");
                }

                var model = models.FirstOrDefault(candidate =>
                    string.Equals(candidate.Provider, providerId, StringComparison.Ordinal) &&
                    string.Equals(candidate.Id, modelId, StringComparison.Ordinal));
                if (model is null)
                    return new(PiSharpModelSelectionStatus.UnknownModel, "Unknown provider or model.");
                if (!model.Available)
                {
                    var authentication = await modelRuntime.ResolveAuthAsync(providerId,
                        useRuntimeOverride: true, cancellationToken).ConfigureAwait(false);
                    return authentication.Authenticated
                        ? new(PiSharpModelSelectionStatus.UnknownModel, "The requested model is unavailable.")
                        : new(PiSharpModelSelectionStatus.AuthenticationUnavailable,
                            "Authentication is unavailable for the requested model.");
                }

                await modelTransitions.SelectRpcModelAsync(model, cancellationToken).ConfigureAwait(false);
                return new(PiSharpModelSelectionStatus.Selected);
            },
            () => ToPublicThinkingLevel(modelBinding.Thinking),
            async (level, cancellationToken) =>
            {
                var requested = ToRuntimeThinkingLevel(level);
                var effective = getRun().IsActive
                    ? await modelTransitions.SetRpcThinkingLevelDuringRunAsync(requested, cancellationToken).ConfigureAwait(false)
                    : await modelTransitions.SetRpcThinkingLevelAsync(requested, cancellationToken).ConfigureAwait(false);
                return ToPublicThinkingLevel(effective);
            },
            abortCurrentRun,
            async cancellationToken =>
            {
                try
                {
                    await cancelCurrentRunAsync(cancellationToken).ConfigureAwait(false);
                    var result = await getRun().CompactWithResultAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return new PiSharpCompactionResult(result is null
                        ? PiSharpCompactionStatus.NotNeeded
                        : PiSharpCompactionStatus.Compacted);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new PiSharpCompactionResult(PiSharpCompactionStatus.Cancelled);
                }
                catch (Exception error)
                {
                    return new PiSharpCompactionResult(PiSharpCompactionStatus.Failed, error.Message);
                }
            },
            requestShutdown);
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

    private static string ToRuntimeThinkingLevel(PiSharpThinkingLevel level) => level switch
    {
        PiSharpThinkingLevel.Off => "off",
        PiSharpThinkingLevel.Minimal => "minimal",
        PiSharpThinkingLevel.Low => "low",
        PiSharpThinkingLevel.Medium => "medium",
        PiSharpThinkingLevel.High => "high",
        PiSharpThinkingLevel.ExtraHigh => "xhigh",
        PiSharpThinkingLevel.Max => "max",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };
}
