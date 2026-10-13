using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Applies Pi's provider request deadline and streaming update idle deadline.</summary>
internal sealed class ProviderRequestTimeoutChatClient : DelegatingChatClient, IProviderToolCallDeltaSource
{
    private readonly IProviderToolCallDeltaSource? _toolCallDeltaSource;
    private readonly int _requestTimeoutMs;
    private readonly int _idleTimeoutMs;

    public ProviderRequestTimeoutChatClient(IChatClient inner, int requestTimeoutMs, int idleTimeoutMs) : base(inner)
    {
        _toolCallDeltaSource = inner as IProviderToolCallDeltaSource;
        _requestTimeoutMs = requestTimeoutMs;
        _idleTimeoutMs = idleTimeoutMs;
    }

    public IProviderToolCallDeltaCapture? BeginToolCallDeltaCapture() =>
        _toolCallDeltaSource?.BeginToolCallDeltaCapture();

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeoutMs);
        var started = Stopwatch.GetTimestamp();
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, timeout.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The provider request timed out after {_requestTimeoutMs} milliseconds.", error);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The provider request timed out after {_requestTimeoutMs} milliseconds.", error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= _requestTimeoutMs)
            throw new TimeoutException($"The provider request timed out after {_requestTimeoutMs} milliseconds.");
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(_requestTimeoutMs);
        var started = Stopwatch.GetTimestamp();
        var lastWireActivity = started;
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestTimeout.Token);
        if (_idleTimeoutMs > 0) idleTimeout.CancelAfter(_idleTimeoutMs);
        using var wireActivity = ProviderWireActivity.Observe(() =>
        {
            if (_idleTimeoutMs <= 0) return;
            Interlocked.Exchange(ref lastWireActivity, Stopwatch.GetTimestamp());
            try { idleTimeout.CancelAfter(_idleTimeoutMs); }
            catch (ObjectDisposedException) { }
        });
        var source = base.GetStreamingResponseAsync(messages, options, idleTimeout.Token);
        await using var updates = source.GetAsyncEnumerator(idleTimeout.Token);
        while (true)
        {
            bool moved;
            try
            {
                moved = await updates.MoveNextAsync();
            }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreateTimeout(requestTimeout, idleTimeout, started, Interlocked.Read(ref lastWireActivity), error);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                (requestTimeout.IsCancellationRequested || idleTimeout.IsCancellationRequested))
            {
                throw CreateTimeout(requestTimeout, idleTimeout, started, Interlocked.Read(ref lastWireActivity), error);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var requestExpired = Stopwatch.GetElapsedTime(started).TotalMilliseconds >= _requestTimeoutMs;
            var idleExpired = _idleTimeoutMs > 0 && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastWireActivity)).TotalMilliseconds >= _idleTimeoutMs;
            if (requestTimeout.IsCancellationRequested || idleTimeout.IsCancellationRequested || requestExpired || idleExpired)
                throw CreateTimeout(requestTimeout, idleTimeout, started, Interlocked.Read(ref lastWireActivity));
            if (!moved) yield break;
            yield return updates.Current;
        }
    }

    private TimeoutException CreateTimeout(CancellationTokenSource requestTimeout,
        CancellationTokenSource idleTimeout, long started, long lastWireActivity, Exception? error = null)
    {
        var reason = requestTimeout.IsCancellationRequested || Stopwatch.GetElapsedTime(started).TotalMilliseconds >= _requestTimeoutMs
            ? $"The provider request timed out after {_requestTimeoutMs} milliseconds."
            : _idleTimeoutMs > 0 && (idleTimeout.IsCancellationRequested ||
                Stopwatch.GetElapsedTime(lastWireActivity).TotalMilliseconds >= _idleTimeoutMs)
                ? $"The provider stream produced no update for {_idleTimeoutMs} milliseconds."
                : $"The provider request timed out after {_requestTimeoutMs} milliseconds.";
        return new TimeoutException(reason, error);
    }
}
