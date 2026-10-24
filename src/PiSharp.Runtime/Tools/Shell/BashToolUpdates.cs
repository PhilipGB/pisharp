using System.Runtime.ExceptionServices;
using System.Text;

namespace PiSharp.Runtime.Tools;

/// <summary>Publishes bounded cumulative model-tool snapshots while retaining deltas for native presentation.</summary>
internal sealed class BashToolUpdates : IAsyncDisposable
{
    private static readonly TimeSpan s_interval = TimeSpan.FromMilliseconds(100);
    private readonly ShellOutputBuffer _output;
    private readonly Action<BashToolUpdate> _publish;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _timerGate = new();
    private readonly ITimer _timer;
    private readonly StringBuilder _delta = new();
    private Task _timerWork = Task.CompletedTask;
    private Task? _stopTask;
    private ExceptionDispatchInfo? _failure;
    private long _lastPublish;
    private bool _hasPublished;
    private bool _dirty;
    private bool _omitted;
    private bool _completed;
    private bool _stopped;

    public BashToolUpdates(ShellOutputBuffer output, Action<BashToolUpdate> publish, TimeProvider? clock = null)
    {
        _output = output;
        _publish = publish;
        _clock = clock ?? TimeProvider.System;
        _timer = _clock.CreateTimer(_ => ScheduleTimerWork(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_completed) return;
            var decoded = await _output.AppendAsync(data, cancellationToken);
            _delta.Append(decoded);
            if (_delta.Length > 50 * 1024)
            {
                var remove = _delta.Length - 50 * 1024;
                if (char.IsLowSurrogate(_delta[remove])) remove++;
                _delta.Remove(0, remove);
                _omitted = true;
            }
            _dirty = true;
            var remaining = _hasPublished ? s_interval - _clock.GetElapsedTime(_lastPublish) : TimeSpan.Zero;
            if (remaining <= TimeSpan.Zero)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                await PublishUnsafeAsync();
            }
            else _timer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync()
    {
        await StopTimerAsync();
        await _gate.WaitAsync();
        try
        {
            if (!_completed)
            {
                await PublishUnsafeAsync();
                _completed = true;
            }
        }
        finally { _gate.Release(); }
        _failure?.Throw();
    }

    private void ScheduleTimerWork()
    {
        lock (_timerGate)
        {
            if (_stopped) return;
            _timerWork = PublishAfterAsync(_timerWork);
        }
    }

    private async Task PublishAfterAsync(Task previous)
    {
        await previous;
        await _gate.WaitAsync();
        try { await PublishUnsafeAsync(); }
        finally { _gate.Release(); }
    }

    private async Task PublishUnsafeAsync()
    {
        if (!_dirty || _completed || _failure is not null) return;
        _dirty = false;
        _lastPublish = _clock.GetTimestamp();
        _hasPublished = true;
        var delta = _delta.ToString();
        _delta.Clear();
        if (_omitted)
        {
            delta = "\n[Earlier live output omitted; the final result contains the retained output.]\n" + delta;
            _omitted = false;
        }
        try
        {
            var snapshot = await _output.SnapshotAsync();
            object details = snapshot.Truncation is { Truncated: true } truncation
                ? new { truncation, fullOutputPath = snapshot.FullOutputPath }
                : snapshot.FullOutputPath is not null ? new { fullOutputPath = snapshot.FullOutputPath }
                : new Dictionary<string, object?>();
            _publish(new(snapshot.Output, details, delta));
        }
        catch (Exception error) { _failure = ExceptionDispatchInfo.Capture(error); }
    }

    private Task StopTimerAsync()
    {
        lock (_timerGate)
        {
            if (_stopTask is not null) return _stopTask;
            _stopped = true;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return _stopTask = FinishTimerAsync(_timerWork);
        }
    }

    private async Task FinishTimerAsync(Task pending)
    {
        await _timer.DisposeAsync();
        await pending;
    }

    public async ValueTask DisposeAsync()
    {
        await StopTimerAsync();
        await _gate.WaitAsync();
        try { _completed = true; }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}
