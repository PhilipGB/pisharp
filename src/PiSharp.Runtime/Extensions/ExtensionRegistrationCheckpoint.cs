namespace PiSharp.Runtime.Extensions;

public sealed partial class ExtensionRegistration
{
    internal Action CreateConfigurationCheckpoint()
    {
        lock (_toolGate)
            lock (_mcpGate)
                lock (_providerGate)
                {
                    var owned = _ownedToolNames.ToDictionary(pair => pair.Key,
                        pair => new HashSet<string>(pair.Value, pair.Value.Comparer), _ownedToolNames.Comparer);
                    var toolCandidates = _toolCandidates.ToDictionary(pair => pair.Key,
                        pair => pair.Value.ToList(), _toolCandidates.Comparer);
                    Action[] restore = [Capture(_tools), Capture(_toolDefinitions), Capture(_toolSourceInfo),
                Capture(_toolRenderers), Capture(_commands), Capture(_mcpServers),
                Capture(_userBashHandlers), Capture(_toolCallHooks), Capture(_toolResultHooks),
                Capture(_inputHandlers), Capture(_beforeProviderRequestHandlers), Capture(_beforeProviderHeadersHandlers),
                Capture(_afterProviderResponseHandlers), Capture(_providerStreamEventHandlers),
                Capture(_contextTransforms), Capture(_contextHandlers), Capture(_contextWithSystemHandlers),
                Capture(_lifecycleHandlers), Capture(_beforeAgentStartHandlers),
                Capture(_resourceDiscoveryHandlers), Capture(_projectTrustHandlers),
                Capture(_shortcuts),
                Capture(_extensionFlags),
                Capture(_sessionStartHandlers), Capture(_sessionShutdownHandlers),
                Capture(_sessionInfoChangedHandlers), Capture(_sessionCompactHandlers),
                Capture(_sessionCompactFailedHandlers), Capture(_agentStartHandlers), Capture(_agentEndHandlers),
                Capture(_turnStartHandlers), Capture(_turnEndHandlers), Capture(_toolExecutionStartHandlers),
                Capture(_messageStartHandlers), Capture(_messageUpdateHandlers), Capture(_messageEndHandlers),
                Capture(_toolExecutionUpdateHandlers), Capture(_toolExecutionEndHandlers),
                Capture(_turnEndBoundaryHandlers), Capture(_agentBeforeSettleHandlers), Capture(_agentSettledHandlers),
                Capture(_modelSelectHandlers), Capture(_thinkingLevelSelectHandlers),
                Capture(_sessionBeforeSwitchHandlers), Capture(_sessionBeforeForkHandlers),
                Capture(_sessionBeforeTreeHandlers), Capture(_sessionTreeHandlers),
                Capture(_sessionBeforeCompactHandlers),
                _virtualModels.CreateConfigurationCheckpoint(),
                CreateExtensionProviderCheckpoint()];
                    return () =>
                    {
                        lock (_toolGate)
                            lock (_mcpGate)
                                lock (_providerGate)
                                {
                                    foreach (var reset in restore) reset();
                                    _ownedToolNames.Clear();
                                    foreach (var pair in owned) _ownedToolNames.Add(pair.Key, pair.Value);
                                    _toolCandidates.Clear();
                                    foreach (var pair in toolCandidates) _toolCandidates.Add(pair.Key, pair.Value);
                                }
                    };
                }
    }

    private static Action Capture<TKey, TValue>(Dictionary<TKey, TValue> target) where TKey : notnull
    {
        var saved = new Dictionary<TKey, TValue>(target, target.Comparer);
        return () =>
        {
            target.Clear();
            foreach (var pair in saved) target.Add(pair.Key, pair.Value);
        };
    }

    private static Action Capture<T>(List<T> target)
    {
        var saved = target.ToArray();
        return () => { target.Clear(); target.AddRange(saved); };
    }
}
