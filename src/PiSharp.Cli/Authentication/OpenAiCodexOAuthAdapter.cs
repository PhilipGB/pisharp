using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PiSharp.Cli;

/// <summary>Implements Pi's ChatGPT account OAuth flow for the Codex Responses provider.</summary>
public sealed class OpenAiCodexOAuthAdapter : IProviderOAuthAdapter
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthBaseUrl = "https://auth.openai.com";
    private const string AuthorizeUrl = AuthBaseUrl + "/oauth/authorize";
    private const string TokenUrl = AuthBaseUrl + "/oauth/token";
    private const string BrowserRedirectUri = "http://localhost:1455/auth/callback";
    private const string DeviceUserCodeUrl = AuthBaseUrl + "/api/accounts/deviceauth/usercode";
    private const string DeviceTokenUrl = AuthBaseUrl + "/api/accounts/deviceauth/token";
    private const string DeviceVerificationUri = AuthBaseUrl + "/codex/device";
    private const string DeviceRedirectUri = AuthBaseUrl + "/deviceauth/callback";
    private const string AccountClaim = "https://api.openai.com/auth";
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromMinutes(15);
    private static readonly IReadOnlyList<ProviderOAuthLoginMethod> Methods =
    [
        new("browser", "Browser login (default)"),
        new("device_code", "Device code login (headless)")
    ];

    private readonly HttpClient _http;
    private readonly IProviderOAuthCallbackServerFactory _callbackServers;
    private readonly Action<Uri> _openBrowser;

    public OpenAiCodexOAuthAdapter(HttpClient http, IProviderOAuthCallbackServerFactory? callbackServers = null,
        Action<Uri>? openBrowser = null)
    {
        _http = http;
        _callbackServers = callbackServers ?? new OpenAiCodexOAuthCallbackServerFactory();
        _openBrowser = openBrowser ?? OpenBrowser;
    }

    public string ProviderId => "openai-codex";
    public IReadOnlyList<ProviderOAuthLoginMethod> LoginMethods => Methods;

    public async Task<StoredCredential> LoginAsync(string method, IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        return method switch
        {
            "browser" => await LoginBrowserAsync(interaction, cancellationToken).ConfigureAwait(false),
            "device_code" => await LoginDeviceCodeAsync(interaction, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown OpenAI Codex login method '{method}'.", nameof(method))
        };
    }

    public async Task<StoredCredential> RefreshAsync(StoredCredential credential,
        CancellationToken cancellationToken)
    {
        if (credential.Type != "oauth" || string.IsNullOrWhiteSpace(credential.Refresh))
            throw new InvalidDataException("OpenAI Codex OAuth credential has no refresh token.");
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credential.Refresh,
                ["client_id"] = ClientId
            })
        };
        return await ReadTokenResponseAsync(request, "refresh", cancellationToken).ConfigureAwait(false);
    }

    public bool IsValidCredential(StoredCredential credential, out string reason)
    {
        if (credential.Type != "oauth" || string.IsNullOrWhiteSpace(credential.Access) ||
            string.IsNullOrWhiteSpace(credential.Refresh) || credential.Expires is null ||
            string.IsNullOrWhiteSpace(credential.AccountId))
        {
            reason = "required OAuth token fields are missing";
            return false;
        }
        var accountId = TryGetAccountId(credential.Access);
        if (accountId is null || !accountId.Equals(credential.AccountId, StringComparison.Ordinal))
        {
            reason = "access token has no matching ChatGPT account ID";
            return false;
        }
        reason = "";
        return true;
    }

    private async Task<StoredCredential> LoginBrowserAsync(IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var redirectUri = new Uri(BrowserRedirectUri);
        IProviderOAuthCallbackServer callback;
        try { callback = _callbackServers.Start(redirectUri, state); }
        catch (Exception error) when (error is HttpListenerException or InvalidOperationException)
        {
            throw new InvalidOperationException("Could not start the OpenAI Codex browser callback listener. Use device code login instead.", error);
        }

        await using (callback.ConfigureAwait(false))
        {
            var authorizeUri = BuildAuthorizationUri(redirectUri, state, challenge);
            interaction.Notify(new ProviderOAuthNotice("auth_url",
                "Open the authorization URL in a browser and complete the ChatGPT sign-in.", authorizeUri));
            try { _openBrowser(authorizeUri); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or NotSupportedException)
            {
                interaction.Notify(new ProviderOAuthNotice("message",
                    "The browser could not be opened automatically; open the authorization URL manually.", authorizeUri));
            }
            var code = await callback.WaitForCodeAsync(cancellationToken).ConfigureAwait(false);
            return await ExchangeCodeAsync(code, verifier, redirectUri, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<StoredCredential> LoginDeviceCodeAsync(IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, DeviceUserCodeUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { client_id = ClientId }),
                Encoding.UTF8, "application/json")
        };
        using var startResponse = await _http.SendAsync(startRequest, cancellationToken).ConfigureAwait(false);
        if (!startResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI Codex device-code request failed ({(int)startResponse.StatusCode}).");
        using var startJson = await ReadJsonAsync(startResponse, "device-code response", cancellationToken).ConfigureAwait(false);
        var root = startJson.RootElement;
        var deviceAuthId = GetString(root, "device_auth_id");
        var userCode = GetString(root, "user_code");
        var interval = GetNumber(root, "interval");
        if (deviceAuthId is null || userCode is null || interval is null || interval < 0)
            throw new InvalidDataException("OpenAI Codex returned an invalid device-code response.");

        interaction.Notify(new ProviderOAuthNotice("device_code",
            "Enter this code on the OpenAI Codex device page to authorize PiSharp.",
            new Uri(DeviceVerificationUri), userCode,
            checked((int)Math.Min(Math.Ceiling(interval.Value), int.MaxValue)),
            (int)DeviceTimeout.TotalSeconds));
        var deadline = DateTimeOffset.UtcNow + DeviceTimeout;
        var delaySeconds = interval.Value;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pollRequest = new HttpRequestMessage(HttpMethod.Post, DeviceTokenUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    device_auth_id = deviceAuthId,
                    user_code = userCode
                }), Encoding.UTF8, "application/json")
            };
            using var pollResponse = await _http.SendAsync(pollRequest, cancellationToken).ConfigureAwait(false);
            if (pollResponse.IsSuccessStatusCode)
            {
                using var pollJson = await ReadJsonAsync(pollResponse, "device authorization response", cancellationToken).ConfigureAwait(false);
                var code = GetString(pollJson.RootElement, "authorization_code");
                var verifier = GetString(pollJson.RootElement, "code_verifier");
                if (code is null || verifier is null)
                    throw new InvalidDataException("OpenAI Codex returned an invalid device authorization response.");
                return await ExchangeCodeAsync(code, verifier, new Uri(DeviceRedirectUri), cancellationToken)
                    .ConfigureAwait(false);
            }

            var responseText = await pollResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var errorCode = ReadErrorCode(responseText);
            if (pollResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound ||
                errorCode == "deviceauth_authorization_pending")
            {
                // Pi treats 403/404 as pending and accepts the explicit pending response code.
            }
            else if (errorCode == "slow_down")
                delaySeconds += 5;
            else
                throw new InvalidOperationException($"OpenAI Codex device authorization failed ({(int)pollResponse.StatusCode}).");

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var delay = TimeSpan.FromSeconds(Math.Min(delaySeconds, remaining.TotalSeconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("OpenAI Codex device-code login timed out after 15 minutes.");
    }

    private async Task<StoredCredential> ExchangeCodeAsync(string code, string verifier, Uri redirectUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirectUri.ToString()
            })
        };
        return await ReadTokenResponseAsync(request, "exchange", cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredCredential> ReadTokenResponseAsync(HttpRequestMessage request, string operation,
        CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI Codex token {operation} failed ({(int)response.StatusCode}).");
        using var json = await ReadJsonAsync(response, "token response", cancellationToken).ConfigureAwait(false);
        var access = GetString(json.RootElement, "access_token");
        var refresh = GetString(json.RootElement, "refresh_token");
        var expiresIn = GetNumber(json.RootElement, "expires_in");
        if (access is null || refresh is null || expiresIn is null || !double.IsFinite(expiresIn.Value) || expiresIn <= 0)
            throw new InvalidDataException($"OpenAI Codex token {operation} response is missing required fields.");
        var accountId = TryGetAccountId(access);
        if (accountId is null)
            throw new InvalidDataException("OpenAI Codex access token does not contain a ChatGPT account ID.");
        var expires = DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value).ToUnixTimeMilliseconds();
        return new StoredCredential("oauth", Access: access, Refresh: refresh, Expires: expires, AccountId: accountId);
    }

    private static Uri BuildAuthorizationUri(Uri redirectUri, string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["originator"] = "pi"
        };
        return new Uri(AuthorizeUrl + "?" + string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
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
            throw new InvalidDataException($"OpenAI Codex returned invalid JSON for its {description}.", error);
        }
    }

    private static string? GetString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    private static double? GetNumber(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var child)) return null;
        if (child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out var number)) return number;
        if (child.ValueKind == JsonValueKind.String &&
            double.TryParse(child.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        return null;
    }

    private static string? ReadErrorCode(string responseText)
    {
        try
        {
            using var json = JsonDocument.Parse(responseText);
            if (!json.RootElement.TryGetProperty("error", out var error)) return null;
            if (error.ValueKind == JsonValueKind.String) return error.GetString();
            return GetString(error, "code");
        }
        catch (JsonException) { return null; }
    }

    private static string? TryGetAccountId(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!json.RootElement.TryGetProperty(AccountClaim, out var auth)) return null;
            var accountId = GetString(auth, "chatgpt_account_id");
            return string.IsNullOrWhiteSpace(accountId) ? null : accountId;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void OpenBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
}

/// <summary>Runs state-checked loopback callbacks for browser-based provider OAuth flows.</summary>
public class ProviderOAuthCallbackServerFactory : IProviderOAuthCallbackServerFactory
{
    public IProviderOAuthCallbackServer Start(Uri redirectUri, string expectedState)
    {
        var prefix = new UriBuilder(redirectUri) { Path = "/", Query = "", Fragment = "" }.Uri.ToString();
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        return new Server(listener, redirectUri.AbsolutePath, expectedState);
    }

    private sealed class Server(HttpListener listener, string callbackPath, string expectedState)
        : IProviderOAuthCallbackServer
    {
        public async Task<string> WaitForCodeAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                var requestUri = context.Request.Url;
                var status = HttpStatusCode.BadRequest;
                string? code = null;
                if (requestUri?.AbsolutePath == callbackPath &&
                    requestUri.Query.Length <= 16 * 1024 &&
                    requestUri.Query.Length > 0)
                {
                    var query = System.Web.HttpUtility.ParseQueryString(requestUri.Query);
                    if (query["state"] == expectedState && !string.IsNullOrWhiteSpace(query["error"]))
                    {
                        status = HttpStatusCode.BadRequest;
                        var error = query["error"];
                        var description = query["error_description"];
                        code = "oauth-error:" + (string.IsNullOrWhiteSpace(description) ? error : error + ": " + description);
                    }
                    else if (query["state"] == expectedState && !string.IsNullOrWhiteSpace(query["code"]))
                    {
                        status = HttpStatusCode.OK;
                        code = query["code"];
                    }
                }
                else if (requestUri?.AbsolutePath != callbackPath)
                    status = HttpStatusCode.NotFound;

                var body = status == HttpStatusCode.OK
                    ? "<!doctype html><html><body>Sign-in complete. You can close this window.</body></html>"
                    : "<!doctype html><html><body>Sign-in callback was invalid. Return to PiSharp and try again.</body></html>";
                context.Response.StatusCode = (int)status;
                context.Response.ContentType = "text/html; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                context.Response.Close();
                if (code?.StartsWith("oauth-error:", StringComparison.Ordinal) == true)
                    throw new InvalidOperationException("Provider authorization was denied.");
                if (code is not null) return code;
            }
        }

        public ValueTask DisposeAsync()
        {
            listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Compatibility name for the callback server used by OpenAI Codex.</summary>
public sealed class OpenAiCodexOAuthCallbackServerFactory : ProviderOAuthCallbackServerFactory { }
