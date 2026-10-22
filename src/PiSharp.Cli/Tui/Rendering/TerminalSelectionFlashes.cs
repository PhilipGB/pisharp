namespace PiSharp.Cli.Tui;

/// <summary>Retains bounded copy feedback with independent expiry under the screen's timer.</summary>
internal sealed class TerminalSelectionFlashes(TimeProvider clock)
{
    private const int MaximumEntries = 64;
    private readonly List<(string Message, DateTimeOffset Expires)> _entries = [];

    public IReadOnlyList<string> Messages => _entries.Select(entry => entry.Message).ToArray();
    public TimeSpan NextExpiry => _entries.Count == 0 ? Timeout.InfiniteTimeSpan :
        TimeSpan.FromTicks(Math.Max(0, (_entries.Min(entry => entry.Expires) - clock.GetUtcNow()).Ticks));

    public void Add(string message, TimeSpan duration)
    {
        if (_entries.Count == MaximumEntries) _entries.RemoveAt(0);
        _entries.Add((TerminalSafeText.Normalize(message), clock.GetUtcNow() + duration));
    }

    public bool Expire() => _entries.RemoveAll(entry => entry.Expires <= clock.GetUtcNow()) > 0;
    public void Clear() => _entries.Clear();
}
