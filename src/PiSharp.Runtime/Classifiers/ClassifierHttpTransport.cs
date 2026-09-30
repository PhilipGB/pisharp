using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Classifiers;

internal sealed class ClassifierHttpTransport(HttpClient http)
{
    public async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> createRequest, ClassifierRequestOptions options,
        CancellationToken cancellationToken)
    {
        if (options.MaxRetries < 0 || options.TimeoutMs < 0 || options.MaxRetryDelayMs < 0 ||
            options.MaxRetryDelayMs is { } maximum && !double.IsFinite(maximum))
            throw new ArgumentException("Invalid classifier request options.");
        var retry = new ProviderRetryPolicy(options.MaxRetries);
        for (var attempt = 0; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options.TimeoutMs is { } timeout) deadline.CancelAfter(timeout);
            TimeSpan? retryAfter = null;
            try
            {
                using var request = createRequest();
                if (options.Headers is { } headers)
                    foreach (var (name, value) in headers)
                    {
                        request.Headers.Remove(name);
                        if (!request.Headers.TryAddWithoutValidation(name, value))
                        {
                            request.Content?.Headers.Remove(name);
                            request.Content?.Headers.TryAddWithoutValidation(name, value);
                        }
                    }
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                retryAfter = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(body, cancellationToken: deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                var failure = error is OperationCanceledException && deadline.IsCancellationRequested
                    ? new TimeoutException($"Classifier request timed out after {options.TimeoutMs}ms.", error) : error;
                if (!retry.CanRetry(failure, attempt, producedOutput: false)) throw failure;
                var delay = retryAfter ?? TimeSpan.FromMilliseconds(Math.Min(1000, 200 * Math.Pow(2, attempt)));
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                if (options.MaxRetryDelayMs is { } limit && delay.TotalMilliseconds > limit)
                    throw new InvalidOperationException("Classifier provider retry delay exceeds the configured maximum.", failure);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
