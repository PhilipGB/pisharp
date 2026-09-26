using System.ClientModel;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Bounded retries for a failed agent attempt, separate from provider-request retries.</summary>
public sealed record AgentRunRetryPolicy
{
    private static readonly Regex RetryableMessage = new(
        "overloaded|currently experiencing high demand|rate.?limit|too many requests|\\b429\\b|\\b5(?:00|02|03|04|20|24)\\b|service.?unavailable|server.?error|internal.?error|provider.?returned.?error|exceeded request buffer limit while retrying upstream|network.?error|connection.?error|connection.?refused|connection.?lost|other side closed|fetch failed|getaddrinfo|ENOTFOUND|EAI_AGAIN|upstream.?connect|reset before headers|socket hang up|socket connection was closed|timed? out|timeout|terminated|websocket.?closed|websocket.?error|ended without|stream ended before message_stop|stream ended before a terminal response event|http2 request did not get a response|retry delay|you can retry your request|try your request again|please retry your request|ResourceExhausted",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NonRetryableMessage = new(
        "GoUsageLimitError|FreeUsageLimitError|Monthly usage limit reached|available balance|insufficient_quota|out of budget|quota exceeded|billing|context.?length|context window|maximum context|too many tokens",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static AgentRunRetryPolicy Default { get; } = new(enabled: true, maxRetries: 3,
        baseDelay: TimeSpan.FromSeconds(2), maxDelay: TimeSpan.FromSeconds(60));
    public static AgentRunRetryPolicy None { get; } = new(enabled: false, maxRetries: 0,
        baseDelay: TimeSpan.Zero, maxDelay: TimeSpan.Zero);

    public bool Enabled { get; }
    public int MaxRetries { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaxDelay { get; }

    public AgentRunRetryPolicy(bool enabled = true, int maxRetries = 3,
        TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(2);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(60);
        if (maxRetries < 0) throw new ArgumentOutOfRangeException(nameof(maxRetries));
        if (BaseDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(baseDelay));
        if (MaxDelay < TimeSpan.Zero || MaxDelay < BaseDelay)
            throw new ArgumentOutOfRangeException(nameof(maxDelay));
        Enabled = enabled;
        MaxRetries = maxRetries;
    }

    public TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        var multiplier = Math.Pow(2, Math.Min(attempt - 1, 30));
        var ticks = BaseDelay.Ticks * multiplier;
        return TimeSpan.FromTicks((long)Math.Min(ticks, MaxDelay.Ticks));
    }

    internal bool CanRetry(Exception error, string? providerError, int retries, bool enabled)
    {
        if (!enabled || retries >= MaxRetries || error is OperationCanceledException ||
            string.IsNullOrWhiteSpace(providerError)) return false;

        var message = providerError + " " + error.Message;
        if (NonRetryableMessage.IsMatch(message)) return false;
        if (HasTransientStatus(error)) return true;
        return RetryableMessage.IsMatch(message);
    }

    private static bool HasTransientStatus(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            var status = current switch
            {
                HttpRequestException { StatusCode: { } code } => (int)code,
                ClientResultException client => client.Status,
                _ => 0
            };
            if (status is 408 or 409 or 429 || status >= 500) return true;
        }
        return false;
    }
}

internal sealed class AgentRunRetryController
{
    private readonly object _gate = new();
    private readonly AgentRunRetryPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _activeDelay;
    private bool _enabled;

    public AgentRunRetryController(AgentRunRetryPolicy policy,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _policy = policy;
        _enabled = policy.Enabled;
        _delay = delay ?? DelayAsync;
    }

    public bool IsRetrying
    {
        get { lock (_gate) return _activeDelay is not null; }
    }

    public int MaxRetries => _policy.MaxRetries;

    public TimeSpan DelayForAttempt(int attempt) => _policy.DelayForAttempt(attempt);

    public bool CanRetry(Exception error, string? providerError, int retries)
    {
        lock (_gate) return _policy.CanRetry(error, providerError, retries, _enabled);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate) _enabled = enabled;
    }

    public void AbortRetry()
    {
        lock (_gate) _activeDelay?.Cancel();
    }

    public async Task<AgentTurnOutcome> RunTurnAsync(
        Func<bool, Action<AgentLifecycleEvent>, IAsyncEnumerable<AgentResponseUpdate>> executeAttempt,
        Action<AgentLifecycleEvent> publish,
        Action<AgentResponseUpdate> observeUpdate,
        Func<string?, int, CancellationToken, Task> omitFailedContext,
        Func<CancellationToken, Task> restoreContext,
        Action flushPending,
        Func<string?> getHead,
        CancellationToken cancellationToken)
    {
        var accepted = false;
        var continuation = false;
        var retries = 0;
        string? attemptStartHead = null;
        var attemptStartPathLength = 0;
        while (true)
        {
            string? providerError = null;
            try
            {
                await foreach (var update in executeAttempt(continuation, item =>
                {
                    if (item.Type == "prompt_accepted") accepted = true;
                    if (item.Type is "prompt_accepted" or "agent_attempt_started")
                    {
                        attemptStartHead = item.RunStartHead;
                        attemptStartPathLength = item.RunStartPathLength ?? 0;
                    }
                    if (item.Type == "model_request_failed") providerError = item.Error;
                    else if (item.Type == "model_request_completed") providerError = null;
                    publish(item);
                    if (item.Type == "model_request_completed" && retries > 0)
                    {
                        publish(new AgentLifecycleEvent("auto_retry_end")
                        { RetrySuccess = true, RetryAttempt = retries });
                        retries = 0;
                    }
                }))
                    observeUpdate(update);

                flushPending();
                if (retries > 0)
                    publish(new AgentLifecycleEvent("auto_retry_end") { RetrySuccess = true, RetryAttempt = retries });
                publish(new("turn_completed") { TurnEndHead = getHead() });
                return AgentTurnOutcome.Completed;
            }
            catch (OperationCanceledException)
            {
                flushPending();
                publish(new(accepted ? "turn_interrupted" : "prompt_rejected",
                    Error: accepted ? null : "Prompt was cancelled.")
                { TurnEndHead = getHead(), WillRetry = false });
                if (retries > 0)
                    publish(new AgentLifecycleEvent("auto_retry_end")
                    { RetrySuccess = false, RetryAttempt = retries, RetryFinalError = "Retry cancelled" });
                throw;
            }
            catch (Exception error)
            {
                flushPending();
                if (!accepted)
                {
                    publish(new("prompt_rejected", Error: error.Message));
                    return AgentTurnOutcome.Rejected;
                }

                var willRetry = CanRetry(error, providerError, retries);
                publish(new("turn_failed", Error: error.Message)
                {
                    TurnEndHead = getHead(),
                    WillRetry = willRetry
                });
                if (!willRetry)
                {
                    if (retries > 0)
                        publish(new AgentLifecycleEvent("auto_retry_end")
                        { RetrySuccess = false, RetryAttempt = retries, RetryFinalError = error.Message });
                    return AgentTurnOutcome.Failed;
                }

                retries++;
                var delay = DelayForAttempt(retries);
                bool ready;
                try
                {
                    ready = await WaitAsync(delay, cancellationToken, async () =>
                    {
                        publish(new AgentLifecycleEvent("auto_retry_start", Error: error.Message)
                        {
                            RetryAttempt = retries,
                            RetryMaxAttempts = MaxRetries,
                            RetryDelayMs = (long)delay.TotalMilliseconds
                        });
                        await omitFailedContext(attemptStartHead, attemptStartPathLength, cancellationToken);
                    });
                }
                catch (Exception setupError) when (setupError is not OperationCanceledException)
                {
                    publish(new AgentLifecycleEvent("auto_retry_end")
                    { RetrySuccess = false, RetryAttempt = retries, RetryFinalError = setupError.Message });
                    return AgentTurnOutcome.Failed;
                }
                if (!ready)
                {
                    publish(new AgentLifecycleEvent("auto_retry_end")
                    { RetrySuccess = false, RetryAttempt = retries, RetryFinalError = "Retry cancelled" });
                    return AgentTurnOutcome.Failed;
                }
                await restoreContext(cancellationToken);
                continuation = true;
            }
        }
    }

    public async Task<bool> WaitAsync(TimeSpan delay, CancellationToken cancellationToken,
        Func<Task>? onScheduled = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate) _activeDelay = linked;
        try
        {
            if (onScheduled is not null) await onScheduled();
            if (linked.IsCancellationRequested) return false;
            await _delay(delay, linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            lock (_gate)
                if (ReferenceEquals(_activeDelay, linked)) _activeDelay = null;
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        else cancellationToken.ThrowIfCancellationRequested();
    }
}

internal enum AgentTurnOutcome
{
    Completed,
    Rejected,
    Failed
}
