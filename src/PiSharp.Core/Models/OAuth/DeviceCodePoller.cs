namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// A single poll result for a device code flow (pinned pi-ai: device-code.ts poll result).
/// </summary>
public abstract record DeviceCodePollResult
{
    /// <summary>The flow completed with a value.</summary>
    public sealed record Complete<TValue>(TValue Value) : DeviceCodePollResult;

    /// <summary>The user has not approved yet.</summary>
    public sealed record Pending : DeviceCodePollResult;

    /// <summary>The server asked to slow down polling.</summary>
    public sealed record SlowDown(int? IntervalSeconds) : DeviceCodePollResult;

    /// <summary>A terminal failure.</summary>
    public sealed record Failed(string Message) : DeviceCodePollResult;
}

/// <summary>
/// Device code flow poller (pinned pi-ai: pollOAuthDeviceCodeFlow). RFC 8628: default 5s
/// interval when the server omits it, slow_down increases the interval by 5s (or to the
/// server-provided minimum), polling stops at the expiry deadline.
/// </summary>
public static class DeviceCodePoller
{
    private const string CancelMessage = "Login cancelled";
    private const string TimeoutMessage = "Device flow timed out";
    private const string SlowDownTimeoutMessage =
        "Device flow timed out after one or more slow_down responses. This is often caused by clock drift in " +
        "WSL or VM environments. Please sync or restart the VM clock and try again.";
    private const int MinimumIntervalMs = 1_000;
    private const int DefaultPollIntervalSeconds = 5;
    private const int SlowDownIntervalIncrementMs = 5_000;

    /// <summary>Polls the device code flow until completion, failure, or expiry.</summary>
    public static async Task<T> PollAsync<T>(
        Func<Task<DeviceCodePollResult>> poll,
        int? intervalSeconds,
        int? expiresInSeconds,
        bool waitBeforeFirstPoll,
        CancellationToken signal)
    {
        var deadline = expiresInSeconds is { } expires
            ? DateTime.UtcNow.AddSeconds(expires)
            : DateTime.MaxValue;
        var intervalMs = Math.Max(MinimumIntervalMs, (intervalSeconds ?? DefaultPollIntervalSeconds) * 1000);
        var slowDownResponses = 0;

        if (waitBeforeFirstPoll && deadline > DateTime.UtcNow)
        {
            await AbortableSleepAsync(Math.Min(intervalMs, (int)(deadline - DateTime.UtcNow).TotalMilliseconds), signal);
        }

        while (DateTime.UtcNow < deadline)
        {
            signal.ThrowIfCancellationRequested();
            var result = await poll();
            if (result is DeviceCodePollResult.Complete<T> complete)
            {
                return complete.Value;
            }

            if (result is DeviceCodePollResult.Failed failed)
            {
                throw new InvalidOperationException(failed.Message);
            }

            if (result is DeviceCodePollResult.SlowDown slowDown)
            {
                slowDownResponses++;
                intervalMs = slowDown.IntervalSeconds is { } serverInterval && serverInterval > 0
                    ? Math.Max(MinimumIntervalMs, serverInterval * 1000)
                    : Math.Max(MinimumIntervalMs, intervalMs + SlowDownIntervalIncrementMs);
            }

            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0)
            {
                break;
            }

            await AbortableSleepAsync(Math.Min(intervalMs, remaining), signal);
        }

        throw new InvalidOperationException(slowDownResponses > 0 ? SlowDownTimeoutMessage : TimeoutMessage);
    }

    /// <summary>Sleeps for the given duration, rejecting with the cancel message on abort.</summary>
    public static Task AbortableSleepAsync(int ms, CancellationToken signal, string cancelMessage = CancelMessage)
    {
        if (signal.IsCancellationRequested)
        {
            return Task.FromException(new InvalidOperationException(cancelMessage));
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = new System.Threading.Timer(
            _ => tcs.TrySetResult(),
            null,
            ms,
            Timeout.Infinite);
        using var registration = signal.Register(
            () => tcs.TrySetException(new InvalidOperationException(cancelMessage)));
        return tcs.Task;
    }
}
