namespace PiSharp.Runtime.Sessions;

/// <summary>
/// Progress accounting for one run's event stream: how many events were handed to the stream and how
/// many the consumer has finished writing.
///
/// A command path (accepted steering, follow-up, abort) waits for the stream to drain before it is
/// observed, so the two counts must describe exactly the same events. A producer therefore reserves its
/// slot <em>before</em> an event can become visible to the consumer, and gives the reservation back if the
/// write never happened. Reserving after the write would let a fast consumer finish an event the producer
/// has not counted yet, leaving the watermark permanently ahead of the consumer and hanging every later
/// drain wait; leaking a reservation for a cancelled write has the same effect.
/// </summary>
internal sealed class EventWatermark
{
    private readonly object _gate = new();
    private long _produced;
    private long _emitted;
    private TaskCompletionSource? _drained;

    public long Produced
    {
        get { lock (_gate) return _produced; }
    }

    public long Emitted
    {
        get { lock (_gate) return _emitted; }
    }

    /// <summary>
    /// Reserves a slot for an event that is about to be written. Dispose without the event reaching the
    /// stream to roll the reservation back.
    /// </summary>
    public IDisposable Reserve()
    {
        lock (_gate) _produced++;
        return new Reservation(this);
    }

    /// <summary>Records that the consumer finished writing one event.</summary>
    public void NoteEmitted()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _emitted++;
            if (_emitted >= _produced)
            {
                drained = _drained;
                _drained = null;
            }
        }
        drained?.TrySetResult();
    }

    /// <summary>
    /// Releases a pending drain wait when the stream has ended, so acceptance never depends on records
    /// no consumer will write.
    /// </summary>
    public void NoteStreamCompleted()
    {
        TaskCompletionSource? drained;
        lock (_gate)
        {
            _emitted = _produced;
            drained = _drained;
            _drained = null;
        }
        drained?.TrySetResult();
    }

    /// <summary>Completes once every reserved event has been written by the consumer.</summary>
    public Task WaitForDrainAsync()
    {
        lock (_gate)
        {
            if (_emitted >= _produced) return Task.CompletedTask;
            return (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private void Release()
    {
        TaskCompletionSource? drained = null;
        lock (_gate)
        {
            _produced--;
            // The rolled-back event never reached the channel, so a waiter is satisfied if the counts meet.
            if (_emitted >= _produced)
            {
                drained = _drained;
                _drained = null;
            }
        }
        drained?.TrySetResult();
    }

    private sealed class Reservation(EventWatermark owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
        }
    }
}
