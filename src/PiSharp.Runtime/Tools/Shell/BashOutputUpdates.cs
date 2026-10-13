using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;

namespace PiSharp.Runtime;

/// <summary>Coalesces process output into bounded lifecycle updates at Pi's 100 ms cadence.</summary>
internal sealed class BashOutputUpdates : IDisposable
{
    private static readonly TimeSpan s_interval = TimeSpan.FromMilliseconds(100);
    private const int MaxPendingChars = 50 * 1024;
    private const string s_omitted = "\n[Earlier live output omitted; the final result contains the retained output.]\n";
    private readonly object _gate = new();
    private readonly Action<string>? _publish;
    private readonly bool _throttle;
    private readonly StringBuilder _pending = new();
    private readonly Timer? _timer;
    private long _lastPublish;
    private ExceptionDispatchInfo? _failure;
    private bool _omittedPending;
    private bool _completed;

    public BashOutputUpdates(Action<string>? publish, bool throttle = true)
    {
        _publish = publish;
        _throttle = throttle;
        if (publish is not null && throttle)
            _timer = new Timer(_ => PublishPending(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Append(string text)
    {
        if (text.Length == 0 || _publish is null) return;
        lock (_gate)
        {
            if (_completed || _failure is not null) return;
            if (!_throttle)
            {
                EmitUnsafe(text);
                return;
            }
            var elapsed = _lastPublish == 0 ? s_interval : Stopwatch.GetElapsedTime(_lastPublish);
            if (_pending.Length == 0 && elapsed >= s_interval)
            {
                EmitUnsafe(text);
                return;
            }
            _pending.Append(text);
            TrimPendingUnsafe();
            var remaining = s_interval - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            _timer!.Change(remaining, Timeout.InfiniteTimeSpan);
        }
    }

    public void Complete()
    {
        ExceptionDispatchInfo? failure;
        lock (_gate)
        {
            if (!_completed)
            {
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                PublishPendingUnsafe();
                _completed = true;
            }
            failure = _failure;
        }
        failure?.Throw();
    }

    private void PublishPending()
    {
        lock (_gate)
        {
            if (_completed || _failure is not null) return;
            PublishPendingUnsafe();
        }
    }

    private void PublishPendingUnsafe()
    {
        if (_pending.Length == 0) return;
        var text = _pending.ToString();
        _pending.Clear();
        if (_omittedPending)
        {
            text = s_omitted + text;
            _omittedPending = false;
        }
        EmitUnsafe(text);
    }

    private void EmitUnsafe(string text)
    {
        if (_publish is null || _failure is not null) return;
        _lastPublish = Stopwatch.GetTimestamp();
        try { _publish(text); }
        catch (Exception error)
        {
            _failure = ExceptionDispatchInfo.Capture(error);
            _pending.Clear();
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void TrimPendingUnsafe()
    {
        if (_pending.Length <= MaxPendingChars) return;
        var remove = _pending.Length - MaxPendingChars;
        if (remove < _pending.Length && char.IsLowSurrogate(_pending[remove])) remove++;
        _pending.Remove(0, remove);
        _omittedPending = true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _completed = true;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        _timer?.Dispose();
    }
}
