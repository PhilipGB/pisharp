using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

internal sealed record ExtensionHandlerRegistration<T>(T Handler, ResourceSourceInfo SourceInfo) where T : Delegate;

public sealed partial class ExtensionRegistration
{
    internal void ReconcileExtension(ResourceSourceInfo previous, ResourceSourceInfo? selected)
    {
        lock (_toolGate)
            lock (_mcpGate)
            {
                foreach (var name in _toolSourceInfo.Where(pair => pair.Value.Path == previous.Path)
                    .Select(pair => pair.Key).ToArray())
                {
                    if (selected is not null) _toolSourceInfo[name] = selected;
                    else
                    {
                        _toolSourceInfo.Remove(name);
                        _tools.Remove(name);
                        _toolDefinitions.Remove(name);
                        _toolRenderers.Remove(name);
                        foreach (var owned in _ownedToolNames.Values) owned.Remove(name);
                    }
                }
                foreach (var name in _commandInfo.Where(pair => pair.Value.SourceInfo.Path == previous.Path)
                    .Select(pair => pair.Key).ToArray())
                {
                    if (selected is not null) _commandInfo[name] = _commandInfo[name] with { SourceInfo = selected };
                    else { _commandInfo.Remove(name); _commands.Remove(name); }
                }
                if (selected is null)
                {
                    foreach (var name in _mcpServers.Where(pair => pair.Value.ExtensionPath == previous.Path)
                        .Select(pair => pair.Key).ToArray()) _mcpServers.Remove(name);
                    foreach (var model in _virtualModels.Models.Where(model => model.ExtensionPath == previous.Path))
                        _virtualModels.Unregister(model.Model.Provider!, model.Model.Id, previous.Path);
                }
                Reconcile(_userBashHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                Reconcile(_toolCallHooks, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                Reconcile(_toolResultHooks, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                Reconcile(_contextTransforms, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
                Reconcile(_resourceDiscoveryHandlers, entry => entry.ExtensionSource, (entry, source) => entry with { ExtensionSource = source });
                Reconcile(_projectTrustHandlers, entry => entry.SourceInfo, (entry, source) => entry with { SourceInfo = source });
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
