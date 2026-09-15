using System.Net;

namespace PiSharp.Core;

/// <summary>Controls bounded retries for transient model or transport failures.</summary>
public sealed record RetryPolicyOptions(
    bool Enabled,
    int MaxRetries,
    TimeSpan BaseDelay,
    TimeSpan MaximumDelay)
{
    /// <summary>Gets PiSharp's default bounded retry policy.</summary>
    public static RetryPolicyOptions Default { get; } = new(
        Enabled: true,
        MaxRetries: 2,
        BaseDelay: TimeSpan.FromMilliseconds(250),
        MaximumDelay: TimeSpan.FromSeconds(5));

    /// <summary>Gets a policy that never retries.</summary>
    public static RetryPolicyOptions Disabled { get; } = new(
        Enabled: false,
        MaxRetries: 0,
        BaseDelay: TimeSpan.Zero,
        MaximumDelay: TimeSpan.Zero);
}

/// <summary>Provides retry classification and exponential backoff behavior.</summary>
public static class RetryPolicy
{
    /// <summary>Determines whether an exception is safe to retry.</summary>
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || exception is OperationCanceledException)
        {
            return false;
        }

        return exception switch
        {
            HttpRequestException requestException => IsTransientStatus(requestException.StatusCode),
            IOException => true,
            TimeoutException => true,
            _ => false,
        };
    }

    /// <summary>Calculates deterministic exponential delay for a retry number starting at one.</summary>
    public static TimeSpan GetDelay(int retryNumber, RetryPolicyOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retryNumber, 1);
        ArgumentNullException.ThrowIfNull(options);
        var multiplier = Math.Pow(2, retryNumber - 1);
        var milliseconds = options.BaseDelay.TotalMilliseconds * multiplier;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, options.MaximumDelay.TotalMilliseconds));
    }

    /// <summary>Executes an asynchronous operation with bounded transient retries.</summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);
        var retryNumber = 0;
        while (true)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (
                options.Enabled &&
                retryNumber < options.MaxRetries &&
                IsTransient(exception, cancellationToken))
            {
                retryNumber++;
                await Task.Delay(GetDelay(retryNumber, options), cancellationToken);
            }
        }
    }

    private static bool IsTransientStatus(HttpStatusCode? statusCode) => statusCode switch
    {
        null => true,
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout => true,
        _ => false,
    };
}
