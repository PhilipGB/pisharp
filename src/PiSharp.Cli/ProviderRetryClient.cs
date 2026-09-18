using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Core.Settings;

namespace PiSharp.Cli;

/// <summary>
/// Provider-level request retry (pinned pi-ai utils/provider-retry.ts, retryProviderRequest):
/// mirrors the OpenAI/Anthropic SDK retry policy while keeping the backoff sleep
/// interruptible. The wrapped SDK runs with its own retry disabled, so every retry here is a
/// fresh downstream request (pinned: X-Stainless-Retry-Count stays zero).
///
/// Rules (pinned): the x-should-retry header wins; otherwise retry on HTTP 408/409/429/&ge;500
/// and on transport failures (no status). Retry-After (seconds or HTTP date) and
/// retry-after-ms are honored; server-requested delays above maxRetryDelayMs (60s default)
/// fail immediately. Without a server delay: exponential 0.5*2^n seconds capped at 8s, with
/// up to 25% jitter reduction. Retries happen only before any response content is observed;
/// a failure after the first streamed update surfaces to the turn/compaction layer instead.
/// </summary>
internal sealed class ProviderRetryClient : DelegatingChatClient
{
    private const double ExponentialBaseSeconds = 0.5;
    private const double ExponentialCapSeconds = 8;
    private const double JitterReductionFraction = 0.25;

    private readonly SettingsManager _settings;
    private readonly Random _random = new();

    public ProviderRetryClient(IChatClient innerClient, SettingsManager settings)
        : base(innerClient)
    {
        _settings = settings;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (maxRetries, maxRetryDelayMs) = ResolvePolicy();
        var retriesRemaining = maxRetries;

        while (true)
        {
            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                var delay = GetRetryDelayMs(error, maxRetries - retriesRemaining, maxRetryDelayMs);
                if (delay is null || retriesRemaining <= 0)
                {
                    throw;
                }

                retriesRemaining--;
                await AbortableSleepAsync(delay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (maxRetries, maxRetryDelayMs) = ResolvePolicy();
        var retriesRemaining = maxRetries;
        var yieldedAny = false;

        // C# forbids yield inside a try block with a catch, so retry decisions happen in
        // catch blocks without yields (request start and MoveNext await); the yields sit in
        // a sibling try/finally.
        while (true)
        {
            TimeSpan? pendingDelay = null;
            var retryRequested = false;

            IAsyncEnumerator<ChatResponseUpdate> inner = null!;
            try
            {
                // An immediate failure (before any chunk) is retryable exactly like a first
                // MoveNext failure: same classification, same budget.
                inner = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator();
            }
            catch (Exception error)
            {
                pendingDelay = ClassifyForRetry(error, maxRetries - retriesRemaining, maxRetryDelayMs, cancellationToken, yieldedAny);
                if (pendingDelay is null)
                {
                    throw;
                }

                retriesRemaining--;
                retryRequested = true;
            }

            if (retryRequested)
            {
                // Backoff before the fresh request (interruptible, pinned sleep semantics).
                await AbortableSleepAsync(pendingDelay!.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                while (true)
                {
                    // The inner stream already observes the caller token on each MoveNext.
                    var moveNext = inner.MoveNextAsync();
                    bool moved = false;
                    try
                    {
                        moved = await moveNext.ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        var delay = ClassifyForRetry(error, maxRetries - retriesRemaining, maxRetryDelayMs, cancellationToken, yieldedAny);
                        if (delay is null)
                        {
                            throw;
                        }

                        retriesRemaining--;
                        pendingDelay = delay;
                        retryRequested = true;
                    }

                    if (retryRequested || !moved)
                    {
                        break;
                    }

                    yieldedAny = true;
                    yield return inner.Current;
                }

                if (!retryRequested)
                {
                    yield break;
                }
            }
            finally
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }

            // Backoff before the fresh request (interruptible, pinned sleep semantics).
            await AbortableSleepAsync(pendingDelay!.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Classifies a streaming failure for retry. Returns null when the error must be thrown
    /// (cancellation, failure after the first chunk, non-retryable error, or exhausted
    /// budget); otherwise the backoff before the next attempt.
    /// </summary>
    private TimeSpan? ClassifyForRetry(
        Exception error,
        int retryIndex,
        long maxRetryDelayMs,
        CancellationToken cancellationToken,
        bool yieldedAny)
    {
        if (cancellationToken.IsCancellationRequested || yieldedAny)
        {
            // Cancellation is terminal, and a failure after content left the retry layer:
            // the turn/compaction layer owns recovery from that point.
            return null;
        }

        return GetRetryDelayMs(error, retryIndex, maxRetryDelayMs);
    }

    private (int MaxRetries, long MaxRetryDelayMs) ResolvePolicy()
    {
        // settings.retry.provider: maxRetries defaults to 0 (provider retry is opt-in);
        // maxRetryDelayMs defaults to 60s (pinned getProviderRetrySettings).
        var (_, maxRetries, maxRetryDelayMs) = _settings.GetProviderRetrySettings();
        return (maxRetries ?? 0, maxRetryDelayMs);
    }

    /// <summary>
    /// Computes the backoff for a retryable error, or null when the error must not be
    /// retried. Server-requested delays above the cap throw immediately (pinned
    /// validateServerRetryDelayMs) so an oversized delay never becomes a silent sleep.
    /// </summary>
    private TimeSpan? GetRetryDelayMs(Exception error, int retryIndex, long maxRetryDelayMs)
    {
        var (isProviderError, status, headers) = GetErrorDetails(error);
        if (!isProviderError)
        {
            return null;
        }

        if (headers is not null && headers.TryGetValue("x-should-retry", out var shouldRetry))
        {
            if (string.Equals(shouldRetry, "true", StringComparison.OrdinalIgnoreCase))
            {
                return ExponentialDelay(retryIndex);
            }

            return null;
        }

        var retryable = status is null || status is 408 or 409 or 429 || status >= 500;
        if (!retryable)
        {
            return null;
        }

        if (headers is not null &&
            headers.TryGetValue("retry-after-ms", out var retryAfterMs) &&
            double.TryParse(retryAfterMs, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedMs))
        {
            return ValidateServerDelayMs(parsedMs, maxRetryDelayMs, error.Message);
        }

        if (headers is not null && headers.TryGetValue("retry-after", out var retryAfter))
        {
            if (double.TryParse(retryAfter, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                return ValidateServerDelayMs(seconds * 1000, maxRetryDelayMs, error.Message);
            }

            if (DateTimeOffset.TryParse(retryAfter, out var date))
            {
                return ValidateServerDelayMs(
                    (date - DateTimeOffset.UtcNow).TotalMilliseconds, maxRetryDelayMs, error.Message);
            }
        }

        return ExponentialDelay(retryIndex);
    }

    private TimeSpan ValidateServerDelayMs(double delayMs, long maxRetryDelayMs, string providerMessage)
    {
        if (maxRetryDelayMs > 0 && delayMs > maxRetryDelayMs)
        {
            throw new InvalidOperationException(
                $"Server requested {Math.Ceiling((double)delayMs / 1000)}s retry delay " +
                $"(max: {Math.Ceiling((double)maxRetryDelayMs / 1000)}s). {providerMessage}");
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, delayMs));
    }

    private TimeSpan ExponentialDelay(int retryIndex)
    {
        var exponentialMs = Math.Min(ExponentialBaseSeconds * Math.Pow(2, retryIndex), ExponentialCapSeconds) * 1000;
        var jitteredMs = exponentialMs * (1 - _random.NextDouble() * JitterReductionFraction);
        return TimeSpan.FromMilliseconds(Math.Max(0, jitteredMs));
    }

    /// <summary>
    /// Classifies the failure (pinned isProviderError): SDK/provider errors carry a status
    /// (or none for transport failures); everything else is deterministic and never retried
    /// at the provider level.
    /// </summary>
    private static (bool IsProviderError, int? Status, IReadOnlyDictionary<string, string>? Headers) GetErrorDetails(
        Exception error)
    {
        if (error is ProviderHttpException providerError)
        {
            return (true, providerError.Status, providerError.Headers);
        }

        if (error is ClientResultException resultError)
        {
            return (true, resultError.Status, null);
        }

        if (error is HttpRequestException or IOException)
        {
            // Transport failure: retryable with the exponential delay (pinned status undefined).
            return (true, null, null);
        }

        return (false, null, null);
    }

    private static Task AbortableSleepAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Request aborted", cancellationToken);
        }

        // Task.Delay with the caller token rejects as an OperationCanceledException on abort,
        // matching pinned's abortable Sleep semantics.
        return Task.Delay(delay, cancellationToken);
    }
}
