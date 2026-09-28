using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Collects the ordered OSC color replies for a bounded terminal query.</summary>
internal sealed class TerminalColorQueryController : IDisposable
{
    private const int PaletteSize = 16;
    private const int ExpectedReplies = PaletteSize + 2;
    private static readonly string s_query = BuildQuery();

    private readonly object _gate = new();
    private readonly TextWriter _output;
    private readonly Action<TerminalColorState> _changed;
    private readonly TimeSpan _timeout;
    private readonly Queue<PendingQuery> _pending = new();
    private TerminalColorState _current = new();
    private bool _started;
    private bool _canQueryColors;
    private bool _followsAppearance;
    private bool _disposed;

    public TerminalColorQueryController(TextWriter output, Action<TerminalColorState> changed,
        TimeSpan? timeout = null, TerminalColorState? initialColors = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(changed);
        _output = output;
        _changed = changed;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(100);
        _current = initialColors ?? new();
    }

    public TerminalColorState Current
    {
        get { lock (_gate) return _current; }
    }

    public void Start(bool queryColors, bool followAppearance)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Terminal color querying has already started.");
            _started = true;
            _canQueryColors = queryColors;
            SetFollowAppearanceLocked(queryColors && followAppearance);
            if (queryColors) StartQueryLocked();
        }
    }

    public void SetFollowAppearance(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || !_started) return;
            SetFollowAppearanceLocked(enabled && _canQueryColors);
        }
    }

    public void HandleColorResponse(TerminalColorResponse response)
    {
        TerminalColorState? changed = null;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            var query = _pending.Peek();
            if (query.ResultDelivered && !query.TimedOut) return;

            var key = response.PaletteIndex is { } paletteIndex ? 100 + paletteIndex : response.Slot;
            if (!query.Replied.Add(key)) return;
            switch (response.Slot)
            {
                case 10: query.Foreground = response.Color; break;
                case 11: query.Background = response.Color; break;
                case 4 when response.PaletteIndex is { } index: query.Palette[index] = response.Color; break;
            }

            if (query.Replied.Count == ExpectedReplies)
            {
                query.Timer?.Dispose();
                query.Timer = null;
                changed = DeliverLocked(query, late: query.TimedOut);
            }
        }
        if (changed is not null) _changed(changed);
    }

    public void HandleDeviceAttributes()
    {
        TerminalColorState? changed = null;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            var query = _pending.Dequeue();
            query.Timer?.Dispose();
            changed = DeliverLocked(query, late: query.TimedOut);
        }
        if (changed is not null) _changed(changed);
    }

    public void HandleAppearanceReport(string appearance)
    {
        if (appearance is not ("dark" or "light")) return;
        TerminalColorState? changed = null;
        lock (_gate)
        {
            if (_disposed || !_started || !_followsAppearance) return;
            if (_current.AppearanceReport != appearance)
            {
                _current = _current with { AppearanceReport = appearance };
                changed = _current;
            }
            StartQueryLocked();
        }
        if (changed is not null) _changed(changed);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var query in _pending) query.Timer?.Dispose();
            _pending.Clear();
            if (_started && _followsAppearance) WriteLocked("\u001b[?2031l");
            _followsAppearance = false;
        }
    }

    private void SetFollowAppearanceLocked(bool enabled)
    {
        if (_followsAppearance == enabled) return;
        _followsAppearance = enabled;
        if (_started) WriteLocked(enabled ? "\u001b[?2031h" : "\u001b[?2031l");
    }

    private void StartQueryLocked()
    {
        var query = new PendingQuery();
        _pending.Enqueue(query);
        query.Timer = new Timer(_ => HandleTimeout(query), null, _timeout, Timeout.InfiniteTimeSpan);
        if (WriteLocked(s_query)) return;
        _pending.Dequeue();
        query.Timer.Dispose();
    }

    private void HandleTimeout(PendingQuery query)
    {
        TerminalColorState? changed;
        lock (_gate)
        {
            if (_disposed || query.ResultDelivered) return;
            query.TimedOut = true;
            query.Timer?.Dispose();
            query.Timer = null;
            changed = DeliverLocked(query, late: false);
        }
        if (changed is not null) _changed(changed);
    }

    private TerminalColorState? DeliverLocked(PendingQuery query, bool late)
    {
        if (query.ResultDelivered && !late) return null;
        query.ResultDelivered = true;
        var palette = query.Palette.All(color => color.HasValue)
            ? query.Palette.Select(color => color!.Value).ToArray()
            : null;
        var next = _current with
        {
            Foreground = query.Foreground ?? _current.Foreground,
            Background = query.Background ?? _current.Background,
            Palette = palette ?? _current.Palette
        };
        if (next.SameAs(_current)) return null;
        _current = next;
        return next;
    }

    private bool WriteLocked(string value)
    {
        try
        {
            _output.Write(value);
            _output.Flush();
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string BuildQuery()
    {
        var query = new StringBuilder("\u001b]10;?\u0007\u001b]11;?\u0007");
        for (var index = 0; index < PaletteSize; index++) query.Append("\u001b]4;").Append(index).Append(";?\u0007");
        return query.Append("\u001b[c").ToString();
    }

    private sealed class PendingQuery
    {
        public HashSet<int> Replied { get; } = [];
        public TerminalTheme.Rgb? Foreground { get; set; }
        public TerminalTheme.Rgb? Background { get; set; }
        public TerminalTheme.Rgb?[] Palette { get; } = new TerminalTheme.Rgb?[PaletteSize];
        public Timer? Timer { get; set; }
        public bool TimedOut { get; set; }
        public bool ResultDelivered { get; set; }
    }
}
