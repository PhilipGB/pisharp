using System.Net;

namespace PiSharp.Cli;

/// <summary>
/// A provider HTTP error with the status, response headers, and (truncated) body captured by
/// the shared transport handler. Carrying the headers is what lets the provider retry layer
/// honor Retry-After / x-should-retry exactly like the pinned SDK-level retry
/// (pinned pi-ai retryProviderRequest reads error.headers).
/// </summary>
internal sealed class ProviderHttpException : Exception
{
    public ProviderHttpException(
        int status,
        IReadOnlyDictionary<string, string> headers,
        string body)
        : base(FormatMessage(status, body))
    {
        Status = status;
        Headers = headers;
        Body = body;
    }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>Response headers (case-insensitive lookup).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The truncated response body.</summary>
    public string Body { get; }

    private static string FormatMessage(int status, string body)
    {
        var detail = body.Trim();
        if (detail.Length == 0)
        {
            return $"HTTP {status}";
        }

        if (detail.Length > 2_000)
        {
            detail = detail[..2_000] + "…";
        }

        return $"HTTP {status}: {detail}";
    }
}

/// <summary>
/// Captures provider error responses (HTTP >= 400) as <see cref="ProviderHttpException"/>
/// before the OpenAI SDK would parse them, preserving the response headers for retry-after
/// handling. Successful responses pass through untouched.
/// </summary>
internal sealed class ProviderErrorCaptureHandler : DelegatingHandler
{
    private const int MaxBodyCaptureBytes = 8_192;

    /// <summary>Creates the capture handler over the terminal handler (production passes null).</summary>
    public ProviderErrorCaptureHandler(HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is < HttpStatusCode.BadRequest)
        {
            return response;
        }

        string body = string.Empty;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        if (response.Content?.Headers is { } contentHeaders)
        {
            foreach (var (name, values) in contentHeaders)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        try
        {
            if (response.Content is not null)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length > MaxBodyCaptureBytes)
                {
                    bytes = bytes[..MaxBodyCaptureBytes];
                }

                body = System.Text.Encoding.UTF8.GetString(bytes);
            }
        }
        catch
        {
            // The body is diagnostic context; a failed read must not mask the error itself.
        }
        finally
        {
            response.Dispose();
        }

        throw new ProviderHttpException((int)response.StatusCode, headers, body);
    }
}
