using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Authentication;

namespace PiSharp.Runtime.Mcp;

/// <summary>Serializes OAuth token grants for one MCP server across PiSharp processes.</summary>
internal sealed class McpOAuthRefreshHandler(McpTokenCache.ServerCache tokenCache,
    HttpMessageHandler? innerHandler = null) : DelegatingHandler(innerHandler ?? new HttpClientHandler())
{
    private static readonly TimeSpan s_tokenRequestTimeout = TimeSpan.FromSeconds(15);
    private readonly Uri _serverUrl = new(tokenCache.Key);
    private readonly object _lifecycleGate = new();
    private int _activeGrants;
    private TaskCompletionSource _grantsSettled = CompletedSource();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.Content is null ||
            !string.Equals(request.Content.Headers.ContentType?.MediaType, "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase))
            return await base.SendAsync(request, cancellationToken);

        var fields = ParseForm(await request.Content.ReadAsStringAsync(cancellationToken));
        var grantType = fields.FirstOrDefault(field => field.Key == "grant_type").Value;
        if (grantType is not ("refresh_token" or "authorization_code"))
            return await base.SendAsync(request, cancellationToken);

        using var operation = BeginGrant();
        var lease = await tokenCache.Owner.AcquireRefreshLockAsync(_serverUrl, cancellationToken);
        var transferred = false;
        try
        {
            if (grantType == "refresh_token")
            {
                var current = await tokenCache.Owner.ReadTokensAsync(_serverUrl, cancellationToken);
                var observed = tokenCache.LastObserved;
                var requestedRefreshToken = fields.FirstOrDefault(field => field.Key == "refresh_token").Value;
                var cacheChanged = current is not null && (observed is null ||
                    !string.Equals(current.AccessToken, observed.AccessToken, StringComparison.Ordinal) ||
                    !string.Equals(current.RefreshToken, observed.RefreshToken, StringComparison.Ordinal));
                var refreshTokenChanged = !string.Equals(current?.RefreshToken, requestedRefreshToken,
                    StringComparison.Ordinal);
                if (current is not null && !IsExpired(current) &&
                    (cacheChanged || refreshTokenChanged))
                {
                    var cachedResponse = CachedTokenResponse(current, request);
                    tokenCache.Owner.TrackPendingRefresh(_serverUrl, lease);
                    transferred = true;
                    return cachedResponse;
                }

                if (refreshTokenChanged)
                {
                    if (string.IsNullOrEmpty(current?.RefreshToken))
                        return InvalidGrant(request);
                    fields = fields.Select(field => field.Key == "refresh_token"
                        ? new KeyValuePair<string, string>(field.Key, current.RefreshToken)
                        : field).ToList();
                    request.Content = new FormUrlEncodedContent(fields);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(s_tokenRequestTimeout);
            var response = await base.SendAsync(request, timeout.Token);
            var refreshTokenForGrant = grantType == "refresh_token"
                ? fields.FirstOrDefault(field => field.Key == "refresh_token").Value
                : null;
            if (response.IsSuccessStatusCode &&
                await NormalizeAndValidateTokenResponseAsync(response, grantType, refreshTokenForGrant))
            {
                tokenCache.Owner.TrackPendingRefresh(_serverUrl, lease);
                transferred = true;
            }
            return response;
        }
        finally
        {
            if (!transferred) await lease.DisposeAsync();
        }
    }

    internal async Task WaitForSettledAsync()
    {
        while (true)
        {
            Task grantsSettled;
            lock (_lifecycleGate) grantsSettled = _grantsSettled.Task;
            await grantsSettled;
            await tokenCache.Owner.WaitForPendingRefreshAsync(_serverUrl);
            lock (_lifecycleGate)
                if (_activeGrants == 0 && !tokenCache.Owner.HasPendingRefresh(_serverUrl)) return;
        }
    }

    private IDisposable BeginGrant()
    {
        lock (_lifecycleGate)
        {
            if (_activeGrants++ == 0)
                _grantsSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new GrantScope(this);
    }

    private void EndGrant()
    {
        lock (_lifecycleGate)
            if (--_activeGrants == 0) _grantsSettled.TrySetResult();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private static bool IsExpired(TokenContainer tokens) => tokens.ExpiresIn is int expiresIn &&
        DateTimeOffset.UtcNow >= tokens.ObtainedAt.AddSeconds(expiresIn);

    private static HttpResponseMessage CachedTokenResponse(TokenContainer tokens, HttpRequestMessage request)
    {
        int? expiresIn = null;
        if (tokens.ExpiresIn is int lifetime)
            expiresIn = Math.Max(1, (int)Math.Ceiling((tokens.ObtainedAt.AddSeconds(lifetime) -
                DateTimeOffset.UtcNow).TotalSeconds));
        var document = new TokenResponseDocument(tokens.AccessToken, tokens.RefreshToken, tokens.TokenType,
            expiresIn, tokens.Scope);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(JsonSerializer.Serialize(document), Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage InvalidGrant(HttpRequestMessage request) => new(HttpStatusCode.BadRequest)
    {
        RequestMessage = request,
        Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
    };

    private static async Task<bool> NormalizeAndValidateTokenResponseAsync(HttpResponseMessage response,
        string grantType, string? refreshTokenForGrant)
    {
        try
        {
            // The server may already have rotated its refresh token. Once the response
            // arrived, finish recognizing it even if the original caller was canceled.
            var originalContent = response.Content;
            var bytes = await originalContent.ReadAsByteArrayAsync(CancellationToken.None);
            var token = JsonNode.Parse(bytes);
            if (token is not JsonObject document ||
                document["access_token"] is not JsonValue accessToken ||
                !accessToken.TryGetValue<string>(out var accessTokenValue) || string.IsNullOrEmpty(accessTokenValue) ||
                document["token_type"] is not JsonValue tokenType ||
                !tokenType.TryGetValue<string>(out var tokenTypeValue) ||
                !string.Equals(tokenTypeValue, "Bearer", StringComparison.OrdinalIgnoreCase)) return false;

            if (document.TryGetPropertyValue("refresh_token", out var refreshToken))
            {
                if (refreshToken is null || IsEmptyString(refreshToken))
                {
                    if (grantType == "refresh_token" && !string.IsNullOrEmpty(refreshTokenForGrant))
                        document["refresh_token"] = refreshTokenForGrant;
                    else document.Remove("refresh_token");
                }
                else if (refreshToken is not JsonValue tokenValue ||
                    !tokenValue.TryGetValue<string>(out _)) return false;
            }
            else if (grantType == "refresh_token" && !string.IsNullOrEmpty(refreshTokenForGrant))
                document["refresh_token"] = refreshTokenForGrant;

            if (document.TryGetPropertyValue("scope", out var scope))
            {
                if (scope is null || IsEmptyString(scope)) document.Remove("scope");
                else if (scope is not JsonValue scopeValue || !scopeValue.TryGetValue<string>(out _)) return false;
            }

            if (document.TryGetPropertyValue("expires_in", out var expiresIn) && expiresIn is not null)
            {
                if (IsEmptyString(expiresIn)) document.Remove("expires_in");
                else if (expiresIn is not JsonValue expiryValue ||
                    !expiryValue.TryGetValue<int>(out _)) return false;
            }

            if (document.TryGetPropertyValue("id_token", out var idToken) &&
                (idToken is null || IsEmptyString(idToken))) document.Remove("id_token");

            var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(document));
            foreach (var header in originalContent.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content = content;
            originalContent.Dispose();
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool IsEmptyString(JsonNode node) => node is JsonValue value &&
        value.TryGetValue<string>(out var text) && text.Length == 0;

    private static List<KeyValuePair<string, string>> ParseForm(string value)
    {
        var fields = new List<KeyValuePair<string, string>>();
        foreach (var field in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            var key = separator < 0 ? field : field[..separator];
            var item = separator < 0 ? "" : field[(separator + 1)..];
            fields.Add(new KeyValuePair<string, string>(DecodeFormValue(key), DecodeFormValue(item)));
        }
        return fields;
    }

    private static string DecodeFormValue(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private sealed record TokenResponseDocument(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);

    private sealed class GrantScope(McpOAuthRefreshHandler owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.EndGrant();
        }
    }
}
