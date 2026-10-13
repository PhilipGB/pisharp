namespace PiSharp.Cli.Tui;

/// <summary>Owns clocks and invalidation intervals for active tool rows; the screen serializes access.</summary>
internal sealed class TerminalToolTimers(TimeProvider clock, Action invalidate) : IDisposable
{
    private readonly Dictionary<string, State> _calls = new(StringComparer.Ordinal);

    internal void Start(string id)
    {
        if (!_calls.ContainsKey(id)) _calls.Add(id, new(clock.GetUtcNow()));
    }

    internal string? Progress(string id, bool isError)
    {
        if (!_calls.TryGetValue(id, out var state)) return null;
        state.HasProgress = true;
        if (isError)
        {
            state.EndedAt ??= clock.GetUtcNow();
            state.Interval?.Dispose();
            state.Interval = null;
        }
        else if (state.EndedAt is null)
            state.Interval ??= clock.CreateTimer(_ => invalidate(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        return Text(state);
    }

    internal IReadOnlyList<(string Id, string Text)> Rows() => _calls.Where(pair => pair.Value.HasProgress)
        .Select(pair => (pair.Key, Text(pair.Value))).ToArray();

    internal IReadOnlyList<(string Id, double DurationMs)> Cancel()
    {
        var now = clock.GetUtcNow();
        var rows = _calls.Where(pair => pair.Value.HasProgress)
            .Select(pair => (pair.Key, ((pair.Value.EndedAt ?? now) - pair.Value.StartedAt).TotalMilliseconds)).ToArray();
        Dispose();
        return rows;
    }

    internal double? Complete(string id, double? recordedDuration)
    {
        if (!_calls.Remove(id, out var state)) return recordedDuration;
        state.Interval?.Dispose();
        return recordedDuration ?? ((state.EndedAt ?? clock.GetUtcNow()) - state.StartedAt).TotalMilliseconds;
    }

    public void Dispose()
    {
        foreach (var state in _calls.Values) state.Interval?.Dispose();
        _calls.Clear();
    }

    private string Text(State state) => "Elapsed " + ToolDurationText.Format(
        ((state.EndedAt ?? clock.GetUtcNow()) - state.StartedAt).TotalMilliseconds);

    private sealed class State(DateTimeOffset startedAt)
    {
        internal DateTimeOffset StartedAt { get; } = startedAt;
        internal DateTimeOffset? EndedAt { get; set; }
        internal ITimer? Interval { get; set; }
        internal bool HasProgress { get; set; }
    }
}
