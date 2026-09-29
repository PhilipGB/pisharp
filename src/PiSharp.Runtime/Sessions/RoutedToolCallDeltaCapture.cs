using System.Runtime.CompilerServices;

namespace PiSharp.Runtime.Sessions;

/// <summary>Attaches raw tool deltas to the physical client selected for one routed request.</summary>
internal sealed class RoutedToolCallDeltaCapture : IProviderToolCallDeltaCapture
{
    private readonly TaskCompletionSource<IProviderToolCallDeltaCapture?> _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IProviderToolCallDeltaCapture? _capture;
    private readonly object _gate = new();
    private bool _disposed;

    public Exception? ResponseFailure => Volatile.Read(ref _capture)?.ResponseFailure;

    public void Bind(IProviderToolCallDeltaCapture? capture)
    {
        lock (_gate)
        {
            if (_disposed || _ready.Task.IsCompleted) { capture?.Dispose(); return; }
            _capture = capture;
            _ready.SetResult(capture);
        }
    }

    public async IAsyncEnumerable<ProviderToolCallDelta> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var capture = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (capture is null) yield break;
        await foreach (var delta in capture.ReadAllAsync(cancellationToken).WithCancellation(cancellationToken))
            yield return delta;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _ready.TrySetResult(null);
            _capture?.Dispose();
        }
    }
}
