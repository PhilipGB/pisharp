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
                OrderDictionary(_tools, pair => Rank(_toolSourceInfo[pair.Key].Path));
                OrderDictionary(_toolDefinitions, pair => Rank(_toolSourceInfo[pair.Key].Path));
                OrderDictionary(_toolRenderers, pair => Rank(_toolSourceInfo[pair.Key].Path));
                OrderDictionary(_toolSourceInfo, pair => Rank(pair.Value.Path));
                OrderDictionary(_commands, pair => Rank(_commandInfo[pair.Key].SourceInfo.Path));
                OrderDictionary(_commandInfo, pair => Rank(pair.Value.SourceInfo.Path));
                OrderDictionary(_mcpServers, pair => Rank(pair.Value.ExtensionPath));
                OrderList(_userBashHandlers, entry => Rank(entry.SourceInfo.Path));
                OrderList(_toolCallHooks, entry => Rank(entry.SourceInfo.Path));
                OrderList(_toolResultHooks, entry => Rank(entry.SourceInfo.Path));
                OrderList(_contextTransforms, entry => Rank(entry.SourceInfo.Path));
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
