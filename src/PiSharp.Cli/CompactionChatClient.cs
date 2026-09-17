using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

/// <summary>
/// The compaction authority surface seen by the provider-request wrapper. PiSharp (never the
/// Harness) decides when the authoritative typed session must be compacted and rebuilds the
/// effective model history afterwards.
/// </summary>
internal interface IProviderRequestCompactor
{
    /// <summary>
    /// Compacts the authoritative session when the threshold trigger says it must. Returns
    /// <c>true</c> when a compaction entry was persisted, in which case the effective request
    /// history must be rebuilt before the provider request is forwarded.
    /// </summary>
    Task<bool> EnsureContextFitsAsync(CompactionReason trigger, CancellationToken cancellationToken);

    /// <summary>
    /// Forces compaction after an authoritative provider context-overflow response. Unlike the
    /// threshold trigger this does not require <see cref="PiCompactionPlanner.ShouldCompact"/>;
    /// it still declines when there is nothing meaningful to compact.
    /// </summary>
    Task<bool> ForceCompactAsync(CancellationToken cancellationToken);

    /// <summary>Rebuilds the effective request history from the authoritative typed session.</summary>
    IReadOnlyList<ChatMessage> GetEffectiveHistory();
}

/// <summary>
/// Late-bound target holder so the chat-client stack (built before the session controller
/// exists) can find the compaction authority without a circular dependency.
/// </summary>
internal sealed class CompactionTarget
{
    private IProviderRequestCompactor? _current;

    /// <summary>Gets or sets the active compaction authority (null for ephemeral sessions).</summary>
    public IProviderRequestCompactor? Current
    {
        get => _current;
        set => _current = value;
    }
}

/// <summary>
/// Intercepts every provider/model request made by the Harness function loop. Before each
/// request it lets PiSharp compact the authoritative session when required and rebuilds the
/// outgoing history from the typed context; when a provider request fails with a context
/// overflow it forces one compaction and retries only that request — never the original prompt.
/// Summarization requests bypass this client entirely (they run on the raw model client), so
/// compaction can never recurse into its own summary generation.
/// </summary>
internal sealed class CompactionChatClient : DelegatingChatClient
{
    private readonly Func<IProviderRequestCompactor?> _compactor;

    public CompactionChatClient(IChatClient innerClient, Func<IProviderRequestCompactor?> compactor)
        : base(innerClient)
    {
        _compactor = compactor;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var current = await PrepareRequestAsync(messages, cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.GetResponseAsync(current, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ContextOverflowPolicy.IsContextOverflow(exception))
        {
            // One bounded overflow-compaction recovery attempt for this provider request.
            var recovered = await RecoverFromOverflowAsync(current, cancellationToken).ConfigureAwait(false);
            if (recovered is null)
            {
                throw;
            }

            return await base.GetResponseAsync(recovered, options, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var current = await PrepareRequestAsync(messages, cancellationToken).ConfigureAwait(false);
        var recovered = false;
        while (true)
        {
            // The first update (or failure) is obtained outside the iterator yield path so an
            // overflow before any content can be recovered without yielding inside a catch.
            var attempt = await BeginStreamAttemptAsync(current, options, cancellationToken)
                .ConfigureAwait(false);
            if (attempt.Exception is not null)
            {
                if (recovered || !ContextOverflowPolicy.IsContextOverflow(attempt.Exception))
                {
                    throw attempt.Exception;
                }

                // The provider rejected the request before any content was produced. Compact the
                // authoritative history and retry only this provider request once; if the retry
                // overflows again the error surfaces instead of looping.
                recovered = true;
                var recoveredRequest = await RecoverFromOverflowAsync(current, cancellationToken).ConfigureAwait(false);
                if (recoveredRequest is null)
                {
                    throw attempt.Exception;
                }

                current = recoveredRequest;
                continue;
            }

            if (attempt.Enumerator is { } enumerator)
            {
                try
                {
                    yield return enumerator.Current;
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield return enumerator.Current;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }

            yield break;
        }
    }

    /// <summary>Starts a provider stream and surfaces its first update or its pre-content failure.</summary>
    private async Task<StreamAttempt> BeginStreamAttemptAsync(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator();
        try
        {
            var hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);
            if (!hasCurrent)
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                return new StreamAttempt(null, false, null);
            }

            return new StreamAttempt(enumerator, true, null);
        }
        catch (Exception exception)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            return new StreamAttempt(null, false, exception);
        }
    }

    private sealed record StreamAttempt(
        IAsyncEnumerator<ChatResponseUpdate>? Enumerator,
        bool HasCurrent,
        Exception? Exception);

    private async Task<IReadOnlyList<ChatMessage>> PrepareRequestAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        var compactor = _compactor();
        if (compactor is null)
        {
            return list;
        }

        if (!await compactor.EnsureContextFitsAsync(CompactionReason.Threshold, cancellationToken).ConfigureAwait(false))
        {
            return list;
        }

        // A compaction entry was persisted; the outgoing request must reflect the new
        // effective context (summary + retained suffix) rather than the discarded history.
        return RebuildAfterCompaction(list, compactor.GetEffectiveHistory());
    }

    private async Task<IReadOnlyList<ChatMessage>?> RecoverFromOverflowAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var compactor = _compactor();
        if (compactor is null || !await compactor.ForceCompactAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return RebuildAfterCompaction(messages, compactor.GetEffectiveHistory());
    }

    private static IReadOnlyList<ChatMessage> RebuildAfterCompaction(
        IReadOnlyList<ChatMessage> request,
        IReadOnlyList<ChatMessage> effectiveHistory)
    {
        // The loaded-history prefix was built before compaction and is discarded. The in-flight
        // request messages (new prompt or tool results) are preserved; PiSharp persists them
        // before they can appear in a request, so the composer collapses the duplicated tail.
        var (_, inFlight) = PiSessionRequestComposer.Split(request);
        return PiSessionRequestComposer.Compose(effectiveHistory, inFlight);
    }

}
