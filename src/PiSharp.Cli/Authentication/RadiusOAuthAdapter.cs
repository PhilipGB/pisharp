using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PiSharp.Cli;

/// <summary>Implements Radius gateway browser and device-code OAuth.</summary>
public sealed class RadiusOAuthAdapter : IProviderOAuthAdapter
{
    private const string ClientId = "pi-gateway";
    private const string Scope = "gateway offline_access";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const string RedirectUri = "http://127.0.0.1:1456/oauth/callback";
    private static readonly IReadOnlyList<ProviderOAuthLoginMethod> Methods =
    [
        new("browser", "Sign in with browser (recommended)"),
        new("device-code", "Sign in with device code (when signing in from another device)")
    ];

    private readonly HttpClient _http;
    private readonly Uri _gateway;
    private readonly IProviderOAuthCallbackServerFactory _callbackServers;
    private readonly Action<Uri> _openBrowser;
    private readonly string _gatewayIdentity;

    public RadiusOAuthAdapter(HttpClient http, Uri gateway,
        IProviderOAuthCallbackServerFactory? callbackServers = null, Action<Uri>? openBrowser = null)
    {
        _http = http;
        _gateway = gateway;
        _gatewayIdentity = gateway.GetLeftPart(UriPartial.Authority);
        _callbackServers = callbackServers ?? new ProviderOAuthCallbackServerFactory();
        _openBrowser = openBrowser ?? OpenBrowser;
    }

    public string ProviderId => "radius";
    public IReadOnlyList<ProviderOAuthLoginMethod> LoginMethods => Methods;

    public Task<StoredCredential> LoginAsync(string method, IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken) => method switch
        {
            "browser" => LoginBrowserAsync(interaction, cancellationToken),
            "device-code" => LoginDeviceCodeAsync(interaction, cancellationToken),
            _ => throw new ArgumentException($"Unknown Radius OAuth login method '{method}'.", nameof(method))
        };

    public async Task<StoredCredential> RefreshAsync(StoredCredential credential,
        CancellationToken cancellationToken)
    {
        if (credential.Type != "oauth" || string.IsNullOrWhiteSpace(credential.Refresh))
            throw new InvalidDataException("Radius OAuth credential has no refresh token.");
        using var request = CreateFormRequest("/v1/oauth/token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = credential.Refresh
        });
        return await ReadTokenResponseAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public bool IsValidCredential(StoredCredential credential, out string reason)
    {
        if (credential.Type != "oauth" || string.IsNullOrWhiteSpace(credential.Access) ||
            string.IsNullOrWhiteSpace(credential.Refresh) || credential.Expires is null ||
            !string.Equals(credential.AccountId, _gatewayIdentity, StringComparison.OrdinalIgnoreCase))
        {
            reason = "required Radius OAuth token fields are missing or belong to another gateway";
            return false;
        }
        reason = "";
        return true;
    }

    private async Task<StoredCredential> LoginBrowserAsync(IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var authorizationEndpoint = await DiscoverAuthorizationEndpointAsync(cancellationToken).ConfigureAwait(false);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var callbackUri = new Uri(RedirectUri);
        var authorizeUri = BuildAuthorizationUri(authorizationEndpoint, callbackUri, state, challenge);

        IProviderOAuthCallbackServer callback;
        try { callback = _callbackServers.Start(callbackUri, state); }
        catch (Exception error) when (error is HttpListenerException or InvalidOperationException)
        {
            throw new InvalidOperationException("Could not start the Radius browser callback listener. Use device-code login instead.", error);
        }

        await using (callback.ConfigureAwait(false))
        {
            interaction.Notify(new ProviderOAuthNotice("message", $"Listening for OAuth callback on {callbackUri}"));
            interaction.Notify(new ProviderOAuthNotice("auth_url", "Continue in your browser.", authorizeUri));
            try { _openBrowser(authorizeUri); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or NotSupportedException)
            {
                interaction.Notify(new ProviderOAuthNotice("message",
                    "The browser could not be opened automatically; open the authorization URL manually.", authorizeUri));
            }
            var code = await callback.WaitForCodeAsync(cancellationToken).ConfigureAwait(false);
            using var request = CreateFormRequest("/v1/oauth/token", new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["redirect_uri"] = callbackUri.ToString(),
                ["code"] = code,
                ["code_verifier"] = verifier
            });
            return await ReadTokenResponseAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StoredCredential> LoginDeviceCodeAsync(IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        using var request = CreateFormRequest("/v1/oauth/device", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope
        });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Radius OAuth device authorization failed ({(int)response.StatusCode}).");
        using var json = await ReadJsonAsync(response, "device authorization response", cancellationToken)
            .ConfigureAwait(false);
        var root = json.RootElement;
        var deviceCode = GetString(root, "device_code");
        var userCode = GetString(root, "user_code");
        var verificationUriText = GetString(root, "verification_uri");
        var expiresIn = GetNumber(root, "expires_in");
        var interval = GetNumber(root, "interval") ?? 5;
        if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode) ||
            !TryParseWebUri(verificationUriText, out var verificationUri) || expiresIn is null ||
            expiresIn <= 0 || expiresIn > int.MaxValue || interval < 0 || interval > 3600)
            throw new InvalidDataException("Radius OAuth device authorization response is missing required fields.");

        interaction.Notify(new ProviderOAuthNotice("device_code", "Enter this code to authorize PiSharp.",
            verificationUri, userCode, ToBoundedSeconds(interval), ToBoundedSeconds(expiresIn.Value)));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value);
        var intervalSeconds = Math.Max(1, interval);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pollRequest = CreateFormRequest("/v1/oauth/token", new Dictionary<string, string>
            {
                ["grant_type"] = DeviceGrantType,
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode
            });
            using var pollResponse = await _http.SendAsync(pollRequest, cancellationToken).ConfigureAwait(false);
            if (pollResponse.IsSuccessStatusCode)
                return await ReadTokenResponseAsync(pollResponse, cancellationToken).ConfigureAwait(false);

            var errorCode = await ReadOAuthErrorCodeAsync(pollResponse, cancellationToken).ConfigureAwait(false);
            switch (errorCode)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    intervalSeconds += 5;
                    break;
                case "expired_token":
                    throw new TimeoutException("Radius device authorization expired.");
                case "access_denied":
                    throw new InvalidOperationException("Radius device authorization was denied.");
                default:
                    throw new InvalidOperationException($"Radius OAuth device authorization failed ({(int)pollResponse.StatusCode}).");
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var delay = TimeSpan.FromSeconds(Math.Min(intervalSeconds, remaining.TotalSeconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Radius device-code login timed out.");
    }

    private async Task<Uri> DiscoverAuthorizationEndpointAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_gateway, "/v1/oauth"));
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not load Radius OAuth config ({(int)response.StatusCode}).");
        using var json = await ReadJsonAsync(response, "OAuth discovery response", cancellationToken).ConfigureAwait(false);
        var endpoint = GetString(json.RootElement, "authorizationEndpoint");
        if (!TryParseWebUri(endpoint, out var authorizationUri))
            throw new InvalidDataException("Radius OAuth discovery response has no valid authorizationEndpoint.");
        return authorizationUri;
    }

    private Uri BuildAuthorizationUri(Uri authorizationEndpoint, Uri callbackUri, string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = callbackUri.ToString(),
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["handoff"] = "url",
            ["state"] = state
        };
        var builder = new UriBuilder(authorizationEndpoint)
        {
            Query = string.Join("&", query.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))
        };
        return builder.Uri;
    }

    private async Task<StoredCredential> ReadTokenResponseAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredCredential> ReadTokenResponseAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Radius OAuth token request failed ({(int)response.StatusCode}).");
        using var json = await ReadJsonAsync(response, "token response", cancellationToken).ConfigureAwait(false);
        var access = GetString(json.RootElement, "access_token");
        var refresh = GetString(json.RootElement, "refresh_token");
        var expiresIn = GetNumber(json.RootElement, "expires_in");
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) || expiresIn is null ||
            !double.IsFinite(expiresIn.Value) || expiresIn <= 0)
            throw new InvalidDataException("Radius OAuth token response is missing required fields.");
        var expires = DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value).AddMinutes(-1).ToUnixTimeMilliseconds();
        return new StoredCredential("oauth", Access: access, Refresh: refresh, Expires: expires,
            AccountId: _gatewayIdentity);
    }

    private HttpRequestMessage CreateFormRequest(string path, IReadOnlyDictionary<string, string> values)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_gateway, path))
        {
            Content = new FormUrlEncodedContent(values)
        };
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string description,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Radius returned invalid JSON for its {description}.", error);
        }
    }

    private static async Task<string?> ReadOAuthErrorCodeAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var json = await ReadJsonAsync(response, "OAuth error response", cancellationToken)
                .ConfigureAwait(false);
            return GetString(json.RootElement, "error");
        }
        catch (InvalidDataException) { return null; }
    }

    private static string? GetString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private static double? GetNumber(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var child) ||
            child.ValueKind != JsonValueKind.Number || !child.TryGetDouble(out var number)) return null;
        return double.IsFinite(number) ? number : null;
    }

    private static bool TryParseWebUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out uri!) &&
            uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment)) return true;
        uri = null!;
        return false;
    }

    private static int ToBoundedSeconds(double value) =>
        (int)Math.Clamp(Math.Ceiling(value), 1, int.MaxValue);

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void OpenBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
}
