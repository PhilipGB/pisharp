using System.Net;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Pinned pi-ai isRetryableAssistantError: classifies a failed assistant turn by its
    /// error text so the caller can decide whether to restart the turn. Billing/quota limit
    /// patterns are non-retryable even when the HTTP status looks transient.
    /// </summary>
    public static bool IsRetryableAssistantErrorMessage(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        if (NonRetryableProviderLimitErrorPattern.IsMatch(errorMessage))
        {
            return false;
        }

        return RetryableProviderErrorPattern.IsMatch(errorMessage);
    }

    /// <summary>
    /// Turn-level retryability: the structural transient classification plus the pinned
    /// error-message classifier over the exception chain (MAF may surface provider errors
    /// as wrapped exceptions whose text carries the classification signal).
    /// </summary>
    public static bool IsRetryableTurnFailure(Exception exception, CancellationToken cancellationToken = default)
    {
        if (IsTransient(exception, cancellationToken))
        {
            return true;
        }

        return IsRetryableAssistantErrorMessage(FlattenMessages(exception));
    }

    /// <summary>Joins the exception chain's messages (outer to inner) for pattern classification.</summary>
    public static string FlattenMessages(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                parts.Add(current.Message!);
            }
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Pinned NON_RETRYABLE_PROVIDER_LIMIT_ERROR_PATTERN: subscription/account limits that
    /// are not transient throttles (case-insensitive alternation, pinned buildProviderErrorPattern).
    /// </summary>
    private static readonly Regex NonRetryableProviderLimitErrorPattern = new(
        string.Join("|", new[]
        {
            "GoUsageLimitError", "FreeUsageLimitError",
            "Monthly usage limit reached", "available balance",
            "insufficient_quota", "out of budget", "quota exceeded", "billing",
        }),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Pinned RETRYABLE_PROVIDER_ERROR_PATTERN: generic provider load, HTTP status, and
    /// transport failure signatures (case-insensitive alternation).
    /// </summary>
    private static readonly Regex RetryableProviderErrorPattern = new(
        string.Join("|", new[]
        {
            "overloaded",
            "rate.?limit",
            "too many requests",
            "429",
            "500",
            "502",
            "503",
            "504",
            "524",
            "service.?unavailable",
            "server.?error",
            "internal.?error",
            "provider.?returned.?error",
            "exceeded request buffer limit while retrying upstream",
            "network.?error",
            "connection.?error",
            "connection.?refused",
            "connection.?lost",
            "other side closed",
            "fetch failed",
            "getaddrinfo",
            "ENOTFOUND",
            "EAI_AGAIN",
            "upstream.?connect",
            "reset before headers",
            "socket hang up",
            "socket connection was closed",
            "timed? out",
            "timeout",
            "terminated",
            "websocket.?closed",
            "websocket.?error",
            "ended without",
            "stream ended before message_stop",
            "stream ended before a terminal response event",
            "http2 request did not get a response",
            "retry delay",
            "you can retry your request",
            "try your request again",
            "please retry your request",
            "ResourceExhausted",
        }),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
