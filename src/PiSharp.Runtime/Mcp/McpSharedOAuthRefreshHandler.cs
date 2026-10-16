using System.Net;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace PiSharp.Runtime.Mcp;

/// <summary>Lets canceled callers leave a bounded refresh that other requests still need.</summary>
internal sealed class McpSharedOAuthRefreshHandler(McpTokenCache.ServerCache tokenCache,
    McpOAuthRefreshHandler refreshHandler) : DelegatingHandler(refreshHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.Content?.Headers.ContentType?.MediaType !=
            "application/x-www-form-urlencoded")
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && request.Method == HttpMethod.Post &&
                request.Content?.Headers.ContentType?.MediaType == "application/json")
                McpOAuthRequestContext.RecordRejectedToken(request.Headers.Authorization?.Parameter);
            return response;
        }
        var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var fields = McpOAuthRefreshHandler.ParseForm(System.Text.Encoding.UTF8.GetString(bytes));
        if (!fields.Any(field => field.Key == "grant_type" && field.Value == "refresh_token"))
            return await base.SendAsync(request, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = new ByteArrayContent(bytes)
        };
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in request.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        var pending = RefreshAsync(clone);
        try { return await pending.WaitAsync(cancellationToken); }
        catch
        {
            _ = DisposeUnclaimedResponseAsync(pending);
            throw;
        }
    }

    private async Task<HttpResponseMessage> RefreshAsync(HttpRequestMessage request)
    {
        using var operation = refreshHandler.BeginGrant();
        using (request)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            if (McpOAuthRequestContext.RejectedToken is { } rejected)
            {
                var current = await tokenCache.Owner.ReadTokensAsync(tokenCache.Key, tokenCache.LegacyKey, deadline.Token);
                if (current is not null && current.AccessToken != rejected &&
                    (current.ExpiresIn is not int expires || DateTimeOffset.UtcNow < current.ObtainedAt.AddSeconds(expires)))
                    return McpOAuthRefreshHandler.CachedTokenResponse(current, request);
            }
            var response = await base.SendAsync(request, deadline.Token);
            var pending = tokenCache.Owner.PendingRefresh(tokenCache.Key);
            try
            {
                if (response.StatusCode == HttpStatusCode.OK && pending is not null)
                {
                    // The SDK's canceled caller cannot finish persistence. Preserve issuer/client
                    // binding from the validated grant's cache, then release the cross-process lease.
                    var previous = await tokenCache.GetTokensAsync(CancellationToken.None);
                    using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(CancellationToken.None));
                    var token = document.RootElement;
                    await tokenCache.StoreTokensAsync(new TokenContainer
                    {
                        AccessToken = token.GetProperty("access_token").GetString()!,
                        RefreshToken = String(token, "refresh_token"),
                        TokenType = token.GetProperty("token_type").GetString()!,
                        Scope = String(token, "scope"),
                        ExpiresIn = token.TryGetProperty("expires_in", out var expiry) && expiry.ValueKind == JsonValueKind.Number &&
                            expiry.TryGetInt32(out var lifetime)
                            ? lifetime : null,
                        ObtainedAt = DateTimeOffset.UtcNow,
                        ClientId = previous?.ClientId,
                        ClientSecret = previous?.ClientSecret,
                        TokenEndpointAuthMethod = previous?.TokenEndpointAuthMethod,
                        AuthorizationServer = previous?.AuthorizationServer
                    }, CancellationToken.None);
                }
                return response;
            }
            catch
            {
                if (pending is not null) await tokenCache.Owner.ReleasePendingRefreshAsync(tokenCache.Key, pending);
                response.Dispose();
                throw;
            }
        }
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static async Task DisposeUnclaimedResponseAsync(Task<HttpResponseMessage> pending)
    {
        try { (await pending).Dispose(); }
        catch (Exception) { }
    }
}
