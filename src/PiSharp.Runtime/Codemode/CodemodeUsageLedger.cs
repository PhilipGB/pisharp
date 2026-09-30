using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Codemode;

internal sealed class CodemodeUsageLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, UsageRecord> _records = new(StringComparer.Ordinal);
    public IReadOnlyList<UsageRecord> Records { get { lock (_gate) return _records.Values.ToArray(); } }
    public UsageRecord? Find(string id)
    {
        lock (_gate) return _records.GetValueOrDefault(id);
    }
    public bool Add(string id, UsageRecord usage)
    {
        lock (_gate) return _records.TryAdd(id, usage);
    }
}
