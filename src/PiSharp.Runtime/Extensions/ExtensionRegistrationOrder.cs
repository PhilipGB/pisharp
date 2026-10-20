using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

public sealed partial class ExtensionRegistration
{
    internal void OrderExtensions(IReadOnlyList<ResourceSourceInfo> sources)
    {
        var ranks = sources.Select((source, index) => (source.Path, Rank: index))
            .ToDictionary(entry => entry.Path, entry => entry.Rank, StringComparer.Ordinal);
        lock (_toolGate)
            lock (_mcpGate)
            {
                foreach (var (name, candidates) in _toolCandidates)
                {
                    var ordered = candidates.OrderBy(candidate => Rank(candidate.SourceInfo.Path)).ToArray();
                    candidates.Clear();
                    candidates.AddRange(ordered);
                    SetToolWinner(name, candidates[0]);
                }
                OrderDictionary(_tools, pair => Rank(_toolSourceInfo[pair.Key].Path));
                OrderDictionary(_toolDefinitions, pair => Rank(_toolSourceInfo[pair.Key].Path));
                OrderDictionary(_toolRenderers, pair => Rank(_toolSourceInfo[pair.Key].Path));
                OrderDictionary(_toolSourceInfo, pair => Rank(pair.Value.Path));
                OrderList(_commands, command => Rank(command.Info.SourceInfo.Path));
                OrderList(_shortcuts, shortcut => Rank(shortcut.SourceInfo.Path));
                OrderDictionary(_mcpServers, pair => Rank(pair.Value.ExtensionPath));
                OrderList(_userBashHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_inputHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_toolCallHooks, entry => Rank(entry.SourceInfo.Path));
                OrderList(_toolResultHooks, entry => Rank(entry.SourceInfo.Path));
                OrderList(_contextTransforms, entry => Rank(entry.SourceInfo.Path));
                OrderList(_contextHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_contextWithSystemHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_lifecycleHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_beforeAgentStartHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_sessionInfoChangedHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_sessionCompactHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_sessionCompactFailedHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_agentStartHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_agentEndHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_turnStartHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_messageStartHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_messageUpdateHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_messageEndHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_turnEndHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_turnEndBoundaryHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_agentBeforeSettleHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_agentSettledHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_toolExecutionStartHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_toolExecutionUpdateHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_toolExecutionEndHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_modelSelectHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_thinkingLevelSelectHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_sessionBeforeSwitchHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_sessionBeforeForkHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_sessionBeforeTreeHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_sessionTreeHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_sessionBeforeCompactHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_resourceDiscoveryHandlers, entry => Rank(entry.ExtensionSource.Path));
                OrderList(_projectTrustHandlers, entry => Rank(entry.SourceInfo.Path));
            }

        int Rank(string path) => ranks.GetValueOrDefault(path, int.MaxValue);
    }

    private static void OrderDictionary<TKey, TValue>(Dictionary<TKey, TValue> target,
        Func<KeyValuePair<TKey, TValue>, int> rank) where TKey : notnull
    {
        var ordered = target.OrderBy(rank).ToArray();
        target.Clear();
        foreach (var pair in ordered) target.Add(pair.Key, pair.Value);
    }

    private static void OrderList<T>(List<T> target, Func<T, int> rank)
    {
        var ordered = target.OrderBy(rank).ToArray();
        target.Clear();
        target.AddRange(ordered);
    }
}
