using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

internal sealed record ExtensionHandlerRegistration<T>(T Handler, ResourceSourceInfo SourceInfo) where T : Delegate;

public sealed partial class ExtensionRegistration
{
    internal void ReconcileExtension(ResourceSourceInfo previous, ResourceSourceInfo? selected)
    {
        lock (_toolGate)
            lock (_mcpGate)
                lock (_providerGate)
                {
                    var changedTools = _toolCandidates.Where(pair => pair.Value.Any(candidate =>
                            candidate.SourceInfo.Path == previous.Path))
                        .Select(pair => pair.Key).ToArray();
                    foreach (var name in changedTools)
                    {
                        var candidates = _toolCandidates[name];
                        for (var index = candidates.Count - 1; index >= 0; index--)
                        {
                            if (candidates[index].SourceInfo.Path != previous.Path) continue;
                            if (selected is null) candidates.RemoveAt(index);
                            else candidates[index] = candidates[index] with { SourceInfo = selected };
                        }
                        RefreshToolWinner(name);
                    }
                    for (var index = _commands.Count - 1; index >= 0; index--)
                    {
                        var command = _commands[index];
                        if (command.Info.SourceInfo.Path != previous.Path) continue;
                        if (selected is null) _commands.RemoveAt(index);
                        else _commands[index] = command with { Info = command.Info with { SourceInfo = selected } };
                    }
                    if (selected is null)
                    {
                        foreach (var name in _mcpServers.Where(pair => pair.Value.ExtensionPath == previous.Path)
                            .Select(pair => pair.Key).ToArray()) _mcpServers.Remove(name);
                        foreach (var providerId in _extensionProviders.Where(pair => pair.Value.ExtensionPath == previous.Path)
                            .Select(pair => pair.Key).ToArray()) _extensionProviders.Remove(providerId);
                        foreach (var model in _virtualModels.Models.Where(model => model.ExtensionPath == previous.Path))
                            _virtualModels.Unregister(model.Model.Provider!, model.Model.Id, previous.Path);
                    }
                    Reconcile(_userBashHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_inputHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_beforeProviderRequestHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_beforeProviderHeadersHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_afterProviderResponseHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_providerStreamEventHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_toolCallHooks, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_toolResultHooks, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_contextTransforms, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_lifecycleHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_beforeAgentStartHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionStartHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionShutdownHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionInfoChangedHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionCompactHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionCompactFailedHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_agentStartHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_agentEndHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_turnStartHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_messageStartHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_messageUpdateHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_messageEndHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_turnEndHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_turnEndBoundaryHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_agentBeforeSettleHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_agentSettledHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_toolExecutionStartHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_toolExecutionUpdateHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_toolExecutionEndHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_modelSelectHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_thinkingLevelSelectHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionBeforeSwitchHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionBeforeForkHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionBeforeTreeHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionTreeHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_sessionBeforeCompactHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_resourceDiscoveryHandlers, entry => entry.ExtensionSource, (entry, source) => entry with { ExtensionSource = source });
                    Reconcile(_projectTrustHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_shortcuts, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                    Reconcile(_extensionFlags, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                }

        void Reconcile<T>(List<T> entries, Func<T, ResourceSourceInfo> getSource, Func<T, ResourceSourceInfo, T> update)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                if (getSource(entries[index]).Path != previous.Path) continue;
                if (selected is null) entries.RemoveAt(index);
                else entries[index] = update(entries[index], selected);
            }
        }
    }
}
