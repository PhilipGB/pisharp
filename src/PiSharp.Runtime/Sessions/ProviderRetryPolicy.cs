using System.ClientModel;

namespace PiSharp.Runtime.Sessions;

/// <summary>Opt-in retry policy for a provider request that failed before producing any output.</summary>
public sealed record ProviderRetryPolicy
{
    public static ProviderRetryPolicy None { get; } = new();
    public static ProviderRetryPolicy Default { get; } = new(maxRetries: 2, delay: TimeSpan.FromMilliseconds(200));

    public int MaxRetries { get; }
    public TimeSpan Delay { get; }

    public ProviderRetryPolicy(int maxRetries = 0, TimeSpan delay = default)
    {
        if (maxRetries < 0) throw new ArgumentOutOfRangeException(nameof(maxRetries));
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        MaxRetries = maxRetries;
        Delay = delay;
    }

    internal bool CanRetry(Exception error, int retries, bool producedOutput) =>
        !producedOutput && retries < MaxRetries && (error switch
        {
            ClientResultException result => IsTransientStatus(result.Status),
            HttpRequestException request => request.StatusCode is null || IsTransientStatus((int)request.StatusCode.Value),
            IOException or TimeoutException => true,
            _ => false
        });

    private static bool IsTransientStatus(int status) => status is 0 or 408 or 409 or 429 || status >= 500;
}
