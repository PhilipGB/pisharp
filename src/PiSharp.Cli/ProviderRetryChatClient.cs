using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Amazon.Runtime;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Retries provider requests with an interruptible delay and Pi-compatible server delay limits.</summary>
internal sealed class ProviderRetryChatClient : DelegatingChatClient, IProviderToolCallDeltaSource
{
    private readonly IProviderToolCallDeltaSource? _toolCallDeltaSource;
    private readonly int _maxRetries;
    private readonly double _maxRetryDelayMs;

    public ProviderRetryChatClient(IChatClient inner, int maxRetries, double maxRetryDelayMs) : base(inner)
    {
        if (maxRetries < 0) throw new ArgumentOutOfRangeException(nameof(maxRetries));
        if (!double.IsFinite(maxRetryDelayMs)) throw new ArgumentOutOfRangeException(nameof(maxRetryDelayMs));
        _toolCallDeltaSource = inner as IProviderToolCallDeltaSource;
        _maxRetries = maxRetries;
        _maxRetryDelayMs = maxRetryDelayMs;
    }

    public IProviderToolCallDeltaCapture? BeginToolCallDeltaCapture() =>
        _toolCallDeltaSource?.BeginToolCallDeltaCapture();

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var requestMessages = messages.ToArray();
        for (var retryIndex = 0; ; retryIndex++)
        {
            var responseCapture = ProviderRetryResponseCapture.BeginAttempt();
            try
            {
                return await base.GetResponseAsync(requestMessages, options, cancellationToken);
            }
            catch (Exception error)
            {
                var response = responseCapture.Response;
                responseCapture.Dispose();
                if (!TryGetRetryDelay(error, response, retryIndex, cancellationToken, out var delayMs))
                    throw;
                await DelayAsync(delayMs, cancellationToken);
            }
            finally { responseCapture.Dispose(); }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestMessages = messages.ToArray();
        for (var retryIndex = 0; ; retryIndex++)
        {
            var responseCapture = ProviderRetryResponseCapture.BeginAttempt();
            IAsyncEnumerator<ChatResponseUpdate>? updates = null;
            var moved = false;
            Exception? firstMoveError = null;
            try
            {
                updates = base.GetStreamingResponseAsync(requestMessages, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                moved = await updates.MoveNextAsync();
            }
            catch (Exception error)
            {
                firstMoveError = error;
            }
            var response = responseCapture.Response;
            responseCapture.Dispose();

            if (firstMoveError is not null)
            {
                if (updates is not null)
                {
                    // Cleanup must not hide the original provider failure.
                    try { await updates.DisposeAsync(); }
                    catch { }
                }
                if (!TryGetRetryDelay(firstMoveError, response, retryIndex, cancellationToken, out var delayMs))
                    ExceptionDispatchInfo.Capture(firstMoveError).Throw();
                await DelayAsync(delayMs, cancellationToken);
                continue;
            }

            if (updates is null) throw new InvalidOperationException("The provider returned no stream enumerator.");
            await using (updates)
            {
                if (!moved) yield break;
                yield return updates.Current;
                while (await updates.MoveNextAsync()) yield return updates.Current;
            }
            yield break;
        }
    }

    private bool TryGetRetryDelay(Exception error, ProviderRetryResponse? response, int retryIndex,
        CancellationToken cancellationToken, out double delayMs)
    {
        delayMs = 0;
        if (cancellationToken.IsCancellationRequested || retryIndex >= _maxRetries ||
            !ProviderRequestRetryPolicy.IsRetryable(error, response)) return false;

        var serverDelay = ProviderRequestRetryPolicy.GetServerDelay(response);
        if (serverDelay is { } requestedDelay)
        {
            if (_maxRetryDelayMs > 0 && requestedDelay > _maxRetryDelayMs)
                throw ProviderRequestRetryPolicy.CreateDelayLimitException(requestedDelay, _maxRetryDelayMs, error);
            delayMs = Math.Max(0, requestedDelay);
        }
        else
        {
            delayMs = ProviderRequestRetryPolicy.GetBackoffDelay(retryIndex);
        }
        return true;
    }

    private static async Task DelayAsync(double delayMs, CancellationToken cancellationToken)
    {
        while (delayMs > 0)
        {
            var interval = Math.Min(delayMs, TimeSpan.FromDays(1).TotalMilliseconds);
            await Task.Delay(TimeSpan.FromMilliseconds(interval), cancellationToken);
            delayMs -= interval;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}

internal static class ProviderRequestRetryPolicy
{
    public static bool IsRetryable(Exception error, ProviderRetryResponse? response)
    {
        if (response?.ShouldRetry is "true") return true;
        if (response?.ShouldRetry is "false") return false;

        var status = response?.StatusCode ?? GetStatusCode(error);
        if (status is not null) return IsRetryableStatus(status.Value, null);
        return response is not null || error is ClientResultException or HttpRequestException or
            OperationCanceledException or TimeoutException;
    }

    public static bool IsRetryableStatus(int status, string? shouldRetry)
    {
        if (shouldRetry is "true") return true;
        if (shouldRetry is "false") return false;
        return status is 408 or 409 or 429 or >= 500;
    }

    public static double? GetServerDelay(ProviderRetryResponse? response)
    {
        if (response?.RetryAfterMs is { Length: > 0 } retryAfterMs && TryParseHeaderNumber(retryAfterMs, out var ms))
            return ms;
        if (response?.RetryAfter is not { Length: > 0 } retryAfter) return null;
        if (TryParseHeaderNumber(retryAfter, out var seconds))
            return seconds > double.MaxValue / 1000 ? double.MaxValue : seconds * 1000;
        if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return (date - DateTimeOffset.UtcNow).TotalMilliseconds;
        return null;
    }

    public static double GetBackoffDelay(int retryIndex) =>
        Math.Min(0.5 * Math.Pow(2, retryIndex), 8) * 1000 * (1 - Random.Shared.NextDouble() * 0.25);

    public static Exception CreateDelayLimitException(double delayMs, double maxDelayMs, Exception providerError) =>
        new InvalidOperationException(
            $"Server requested {Math.Ceiling(delayMs / 1000).ToString("0", CultureInfo.InvariantCulture)}s retry delay " +
            $"(max: {Math.Ceiling(maxDelayMs / 1000).ToString("0", CultureInfo.InvariantCulture)}s). {providerError.Message}",
            providerError);

    private static bool TryParseHeaderNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    private static int? GetStatusCode(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var status = current switch
            {
                ClientResultException clientError when clientError.Status > 0 => clientError.Status,
                HttpRequestException { StatusCode: { } httpStatus } => (int)httpStatus,
                AmazonServiceException { StatusCode: not HttpStatusCode.OK } awsError => (int)awsError.StatusCode,
                _ => (int?)null
            };
            if (status is not null) return status;
        }
        return null;
    }
}

internal sealed record ProviderRetryResponse(int StatusCode, string? ShouldRetry, string? RetryAfterMs, string? RetryAfter);

internal static class ProviderRetryResponseCapture
{
    internal sealed class AttemptState
    {
        public ProviderRetryResponse? Response;
    }

    private static readonly AsyncLocal<AttemptState?> s_current = new();

    public sealed class Attempt : IDisposable
    {
        private readonly AttemptState _state;
        private readonly AttemptState? _previous;
        private int _disposed;

        internal Attempt(AttemptState state, AttemptState? previous)
        {
            _state = state;
            _previous = previous;
        }

        public ProviderRetryResponse? Response => Volatile.Read(ref _state.Response);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) s_current.Value = _previous;
        }
    }

    public static Attempt BeginAttempt()
    {
        var previous = s_current.Value;
        var state = new AttemptState();
        s_current.Value = state;
        return new Attempt(state, previous);
    }

    public static void Observe(HttpResponseMessage response)
    {
        var state = s_current.Value;
        if (state is null) return;
        Volatile.Write(ref state.Response, new((int)response.StatusCode,
            GetHeader(response, "x-should-retry"),
            GetHeader(response, "retry-after-ms"),
            GetHeader(response, "retry-after")));
    }

    private static string? GetHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
