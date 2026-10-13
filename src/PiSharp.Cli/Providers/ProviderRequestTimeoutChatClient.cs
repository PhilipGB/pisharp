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
        try
        {
            return await base.GetResponseAsync(messages, options, timeout.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The provider request timed out after {_requestTimeoutMs} milliseconds.", error);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"The provider request timed out after {_requestTimeoutMs} milliseconds.", error);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(_requestTimeoutMs);
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestTimeout.Token);
        if (_idleTimeoutMs > 0) idleTimeout.CancelAfter(_idleTimeoutMs);
        using var wireActivity = ProviderWireActivity.Observe(() =>
        {
            if (_idleTimeoutMs <= 0) return;
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
                throw CreateTimeout(requestTimeout, idleTimeout, error);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                (requestTimeout.IsCancellationRequested || idleTimeout.IsCancellationRequested))
            {
                throw CreateTimeout(requestTimeout, idleTimeout, error);
            }
            if (!moved) yield break;
            yield return updates.Current;
        }
    }

    private TimeoutException CreateTimeout(CancellationTokenSource requestTimeout,
        CancellationTokenSource idleTimeout, Exception error)
    {
        var reason = requestTimeout.IsCancellationRequested
            ? $"The provider request timed out after {_requestTimeoutMs} milliseconds."
            : _idleTimeoutMs > 0 && idleTimeout.IsCancellationRequested
                ? $"The provider stream produced no update for {_idleTimeoutMs} milliseconds."
                : $"The provider request timed out after {_requestTimeoutMs} milliseconds.";
        return new TimeoutException(reason, error);
    }
}
