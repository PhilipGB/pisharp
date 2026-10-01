using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PiSharp.Cli;

/// <summary>Implements Anthropic's Claude Pro/Max browser and copy-code OAuth flows.</summary>
public sealed class AnthropicOAuthAdapter : IProviderOAuthAdapter
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string BrowserRedirectUri = "http://localhost:53692/callback";
    private const string CopyCodeRedirectUri = "https://platform.claude.com/oauth/code/callback";
    private const string Scope = "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<ProviderOAuthLoginMethod> Methods =
    [
        new("browser", "Browser login (default)"),
        new("copy_code", "Copy code login (headless)")
    ];

    private readonly HttpClient _http;
    private readonly IProviderOAuthCallbackServerFactory _callbackServers;
    private readonly Action<Uri> _openBrowser;

    public AnthropicOAuthAdapter(HttpClient http,
        IProviderOAuthCallbackServerFactory? callbackServers = null, Action<Uri>? openBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _callbackServers = callbackServers ?? new ProviderOAuthCallbackServerFactory();
        _openBrowser = openBrowser ?? OpenBrowser;
    }

    public string ProviderId => "anthropic";
    public IReadOnlyList<ProviderOAuthLoginMethod> LoginMethods => Methods;

    public Task<StoredCredential> LoginAsync(string method, IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        return method switch
        {
            "browser" => LoginBrowserAsync(interaction, cancellationToken),
            "copy_code" => LoginCopyCodeAsync(interaction, cancellationToken),
            _ => throw new ArgumentException($"Unknown Anthropic OAuth login method '{method}'.", nameof(method))
        };
    }

    public Task<StoredCredential> RefreshAsync(StoredCredential credential,
        CancellationToken cancellationToken)
    {
        if (credential.Type != "oauth" || string.IsNullOrWhiteSpace(credential.Refresh))
            throw new InvalidDataException("Anthropic OAuth credential has no refresh token.");
        return RequestTokensAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = credential.Refresh
        }, "refresh", cancellationToken);
    }

    public bool IsValidCredential(StoredCredential credential, out string reason)
    {
        if (credential.Type != "oauth" || string.IsNullOrWhiteSpace(credential.Access) ||
            string.IsNullOrWhiteSpace(credential.Refresh) || credential.Expires is null)
        {
            reason = "required Anthropic OAuth token fields are missing";
            return false;
        }
        reason = "";
        return true;
    }

    private async Task<StoredCredential> LoginBrowserAsync(IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var (verifier, challenge) = CreatePkce();
        var redirectUri = new Uri(BrowserRedirectUri);
        var authorizationUri = BuildAuthorizationUri(redirectUri, verifier, challenge);
        IProviderOAuthCallbackServer callback;
        try { callback = _callbackServers.Start(redirectUri, verifier); }
        catch (Exception error) when (error is HttpListenerException or InvalidOperationException)
        {
            interaction.Notify(new ProviderOAuthNotice("auth_url",
                "The local callback could not start. Complete login, then paste the authorization code or redirect URL.", authorizationUri));
            var pasted = await interaction.PromptForCodeAsync(
                "Complete Anthropic login, then paste the authorization code or redirect URL:",
                "code#state", cancellationToken).ConfigureAwait(false);
            var parsed = ParseAuthorizationInput(pasted);
            ValidateAuthorizationInput(parsed, verifier);
            return await ExchangeCodeAsync(parsed.Code!, parsed.State ?? verifier, verifier, redirectUri,
                cancellationToken).ConfigureAwait(false);
        }

        await using (callback.ConfigureAwait(false))
        {
            interaction.Notify(new ProviderOAuthNotice("auth_url", "Complete Anthropic login in your browser.", authorizationUri));
            try { _openBrowser(authorizationUri); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException or NotSupportedException)
            {
                interaction.Notify(new ProviderOAuthNotice("message",
                    "The browser could not be opened automatically; open the authorization URL manually.", authorizationUri));
            }
            var code = await callback.WaitForCodeAsync(cancellationToken).ConfigureAwait(false);
            interaction.Notify(new ProviderOAuthNotice("message", "Exchanging authorization code for tokens."));
            return await ExchangeCodeAsync(code, verifier, verifier, redirectUri, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<StoredCredential> LoginCopyCodeAsync(IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        var (verifier, challenge) = CreatePkce();
        var redirectUri = new Uri(CopyCodeRedirectUri);
        var authorizationUri = BuildAuthorizationUri(redirectUri, verifier, challenge);
        interaction.Notify(new ProviderOAuthNotice("auth_url",
            "Complete login in your browser, then paste the code Anthropic shows.", authorizationUri));
        var pasted = await interaction.PromptForCodeAsync(
            "Paste the code Anthropic shows after you sign in:", "code#state", cancellationToken)
            .ConfigureAwait(false);
        var parsed = ParseAuthorizationInput(pasted);
        ValidateAuthorizationInput(parsed, verifier);
        interaction.Notify(new ProviderOAuthNotice("message", "Exchanging authorization code for tokens."));
        return await ExchangeCodeAsync(parsed.Code!, parsed.State ?? verifier, verifier, redirectUri,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<StoredCredential> ExchangeCodeAsync(string code, string state, string verifier,
        Uri redirectUri, CancellationToken cancellationToken) =>
        RequestTokensAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["state"] = state,
            ["redirect_uri"] = redirectUri.ToString(),
            ["code_verifier"] = verifier
        }, "exchange", cancellationToken);

    private async Task<StoredCredential> RequestTokensAsync(IReadOnlyDictionary<string, string> body,
        string operation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic OAuth token {operation} failed ({(int)response.StatusCode}).");

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
        var root = json.RootElement;
        var access = ReadString(root, "access_token");
        var refresh = ReadString(root, "refresh_token");
        var expiresIn = ReadPositiveInteger(root, "expires_in");
        if (access is null || refresh is null || expiresIn is null)
            throw new InvalidDataException($"Anthropic OAuth token {operation} response is missing required fields.");
        long expiry;
        try { expiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn.Value).Subtract(TimeSpan.FromMinutes(5)).ToUnixTimeMilliseconds(); }
        catch (ArgumentOutOfRangeException error)
        { throw new InvalidDataException($"Anthropic OAuth token {operation} response has an invalid expiry.", error); }
        return new StoredCredential("oauth", Access: access, Refresh: refresh, Expires: expiry);
    }

    private static Uri BuildAuthorizationUri(Uri redirectUri, string verifier, string challenge)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = "true",
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = verifier
        };
        var query = string.Join("&", values.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return new UriBuilder(AuthorizeUrl) { Query = query }.Uri;
    }

    private static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static void ValidateAuthorizationInput((string? Code, string? State) parsed, string verifier)
    {
        if (parsed.State is not null && !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(parsed.State), Encoding.UTF8.GetBytes(verifier)))
            throw new InvalidOperationException("Anthropic OAuth state mismatch.");
        if (string.IsNullOrWhiteSpace(parsed.Code))
            throw new InvalidDataException("Anthropic OAuth authorization input has no code.");
    }

    private static (string? Code, string? State) ParseAuthorizationInput(string input)
    {
        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return ParseQuery(uri.Query);
        if (value.Contains("code=", StringComparison.Ordinal) || value.Contains("state=", StringComparison.Ordinal))
            return ParseQuery(value);
        var delimiter = value.IndexOf('#');
        if (delimiter >= 0)
            return (NullIfWhiteSpace(value[..delimiter]), NullIfWhiteSpace(value[(delimiter + 1)..]));
        return (NullIfWhiteSpace(value), null);
    }

    private static (string? Code, string? State) ParseQuery(string query)
    {
        string? code = null;
        string? state = null;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            var value = Uri.UnescapeDataString(separator < 0 ? "" : part[(separator + 1)..].Replace('+', ' '));
            if (key == "code") code = value;
            else if (key == "state") state = value;
        }
        return (NullIfWhiteSpace(code), NullIfWhiteSpace(state));
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) &&
        child.ValueKind == JsonValueKind.String ? NullIfWhiteSpace(child.GetString()) : null;

    private static long? ReadPositiveInteger(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var child)) return null;
        if (child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out var number) && number > 0) return number;
        if (child.ValueKind == JsonValueKind.String &&
            long.TryParse(child.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0)
            return number;
        return null;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void OpenBrowser(Uri uri) => Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
}
