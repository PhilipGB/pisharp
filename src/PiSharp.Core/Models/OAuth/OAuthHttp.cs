using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// HTTP helpers for OAuth token endpoints (pinned pi-ai fetch wrappers): JSON and
/// form POSTs with a 30s request timeout linked to the login cancellation, and
/// deterministic error formatting.
/// </summary>
public static class OAuthHttp
{
    private static readonly HttpClient SharedClient = new();
    private const int DefaultTimeoutMs = 30_000;
    internal const string CancelledMessage = "Login cancelled";

    private static CancellationToken Link(CancellationToken user, int timeoutMs)
        => CancellationTokenSource.CreateLinkedTokenSource(user, new CancellationTokenSource(timeoutMs).Token).Token;

    private static async Task<string> SendAsync(
        string method,
        string url,
        string? content,
        string contentType,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken userCt,
        int timeoutMs)
    {
        using var cts = new CancellationTokenSource();
        var linked = Link(userCt, timeoutMs);
        using var registration = linked.Register(() => cts.Cancel());
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (content is not null)
            {
                request.Content = new StringContent(content, Encoding.UTF8, contentType);
            }

            if (headers is not null)
            {
                foreach (var (key, value) in headers)
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
            }

            using var response = await SharedClient.SendAsync(request, linked);
            var body = await response.Content.ReadAsStringAsync(linked);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"HTTP request failed. status={(int)response.StatusCode}; url={url}; body={body}");
            }

            return body;
        }
        catch (OperationCanceledException) when (userCt.IsCancellationRequested)
        {
            throw new InvalidOperationException(CancelledMessage);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"OAuth request timed out. url={url}");
        }
    }

    /// <summary>POSTs a JSON body and returns the response text.</summary>
    public static Task<string> PostJsonAsync(
        string url, string body, CancellationToken ct, int timeoutMs = DefaultTimeoutMs)
        => SendAsync("POST", url, body, "application/json",
            new Dictionary<string, string> { ["Accept"] = "application/json" }, ct, timeoutMs);

    /// <summary>POSTs a form-encoded body and returns the response text.</summary>
    public static Task<string> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> form,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null,
        int timeoutMs = DefaultTimeoutMs)
    {
        var body = string.Join('&', form.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var allHeaders = new Dictionary<string, string> { ["Accept"] = "application/json" };
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                allHeaders[key] = value;
            }
        }

        return SendAsync("POST", url, body, "application/x-www-form-urlencoded", allHeaders, ct, timeoutMs);
    }

    /// <summary>GETs a URL and returns the response text as JSON, with optional headers.</summary>
    public static Task<JsonNode?> GetJsonAsync(
        string url,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? headers = null,
        int timeoutMs = DefaultTimeoutMs)
        => SendAsync("GET", url, null, string.Empty, headers, ct, timeoutMs).ContinueWith(
            t => t.IsFaulted ? throw t.Exception!.InnerException! : JsonNode.Parse(t.Result),
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);

    /// <summary>
    /// Reads an OAuth error response body and produces the pinned error details
    /// (error, error_description, or raw text).
    /// </summary>
    public static string FormatOAuthError(int status, string body, string prefix)
    {
        var error = string.Empty;
        var description = string.Empty;
        try
        {
            var node = JsonNode.Parse(body);
            if (node?["error"]?.GetValue<string>() is { } e)
            {
                error = e;
            }

            if (node?["error_description"]?.GetValue<string>() is { } d)
            {
                description = d;
            }
        }
        catch (JsonException)
        {
            description = body;
        }

        var detail = string.Join(": ", new[] { error, description }.Where(s => s.Length > 0));
        return $"{prefix}. status={status}; url={detail}";
    }
}

/// <summary>Builds OAuth credentials from token endpoint responses.</summary>
public static class OAuthCredentialFactory
{
    /// <summary>Creates an OAuth credential from access/refresh/expires_in values.</summary>
    public static OAuthCredential FromToken(
        string access,
        string? refresh,
        double? expiresInSeconds,
        int expirySkewMs = 0,
        string? scope = null,
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        var expires = (long)((DateTime.UtcNow
            .AddSeconds(expiresInSeconds ?? 3600)
            .Subtract(TimeSpan.FromMilliseconds(expirySkewMs)))
            - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        return new OAuthCredential
        {
            Access = access,
            Refresh = refresh ?? string.Empty,
            Expires = expires,
            Scope = scope,
            Extra = extra,
        };
    }
}
