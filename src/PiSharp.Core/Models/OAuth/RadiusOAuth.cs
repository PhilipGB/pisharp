using System.Text.Json.Nodes;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// Radius gateway OAuth (pinned pi-ai: auth/oauth/radius.ts). Gateway discovery at
/// {gateway}/v1/oauth, then PKCE browser login (127.0.0.1:1456/oauth/callback) or a
/// device code flow against {gateway}/v1/oauth/device and {gateway}/v1/oauth/token.
/// </summary>
public sealed class RadiusOAuth : OAuthAuth
{
    private const int CallbackPort = 1456;
    private const string CallbackPath = "/oauth/callback";
    private const string RedirectUri = "http://127.0.0.1:1456/oauth/callback";
    private const int TokenExpirySkewMs = 60_000;
    private const string ClientId = "pi-gateway";
    private const string Scope = "gateway offline_access";
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly string _gateway;

    /// <summary>Creates the flow for the given gateway base URL.</summary>
    public RadiusOAuth(string gateway)
    {
        _gateway = gateway.TrimEnd('/');
    }

    public override async Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
    {
        var method = await interaction.PromptAsync(
            new SelectPromptStep(
                "Select Radius login method:",
                [
                    new SelectableOption("browser", "Browser login (default)"),
                    new SelectableOption("device-code", "Device code login (headless)"),
                ]),
            interaction.Signal);

        return method switch
        {
            "device-code" => await LoginWithDeviceCodeAsync(interaction),
            "browser" => await LoginWithBrowserAsync(interaction),
            _ => throw new InvalidOperationException($"Unknown Radius login method: {method}"),
        };
    }

    private async Task<OAuthCredential> LoginWithBrowserAsync(IAuthInteraction interaction)
    {
        var authorizationEndpoint = await DiscoverAsync(interaction.Signal);
        var pair = Pkce.Generate();
        var state = Guid.NewGuid().ToString();

        var authorize = string.Join('&',
        [
            "response_type=code",
            $"client_id={ClientId}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            $"scope={Uri.EscapeDataString(Scope)}",
            $"code_challenge={pair.Challenge}",
            "code_challenge_method=S256",
            "handoff=url",
            $"state={Uri.EscapeDataString(state)}",
        ]);

        await using var server = await CallbackServer.StartAsync(CallbackPort, CallbackPath, state, "Radius");
        interaction.Notify(new AuthEvent.ProgressEvent($"Listening for OAuth callback on {RedirectUri}"));
        interaction.Notify(new AuthEvent.AuthUrlEvent($"{authorizationEndpoint}?{authorize}", "Continue in your browser."));

        var code = await server.WaitForCodeAsync();
        if (code is null)
        {
            throw interaction.Signal.IsCancellationRequested
                ? new InvalidOperationException("Login cancelled")
                : new InvalidOperationException("OAuth callback did not complete.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["code"] = code.Code,
            ["code_verifier"] = pair.Verifier,
        };
        return await RequestTokenAsync(form, interaction.Signal);
    }

    private async Task<OAuthCredential> LoginWithDeviceCodeAsync(IAuthInteraction interaction)
    {
        var device = await RequestDeviceAuthorizationAsync(interaction.Signal);
        interaction.Notify(new AuthEvent.DeviceCodeEvent(
            device.UserCode, device.VerificationUri, device.Interval, device.ExpiresIn));

        return await DeviceCodePoller.PollAsync<OAuthCredential>(
            async () =>
            {
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = DeviceCodeGrantType,
                    ["client_id"] = ClientId,
                    ["device_code"] = device.DeviceCode,
                };
                try
                {
                    var credential = await RequestTokenAsync(form, interaction.Signal);
                    return new DeviceCodePollResult.Complete<OAuthCredential>(credential);
                }
                catch (OAuthTokenRequestException exception)
                {
                    DeviceCodePollResult? pendingResult = exception.OauthError switch
                    {
                        "authorization_pending" => new DeviceCodePollResult.Pending(),
                        "slow_down" => new DeviceCodePollResult.SlowDown(null),
                        "expired_token" => new DeviceCodePollResult.Failed("Device authorization expired."),
                        "access_denied" => new DeviceCodePollResult.Failed("Device authorization was denied."),
                        _ => null,
                    };
                    return pendingResult ?? new DeviceCodePollResult.Failed(exception.Message);
                }
            },
            device.Interval,
            device.ExpiresIn,
            waitBeforeFirstPoll: false,
            interaction.Signal);
    }

    public override Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken signal)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = credential.Refresh,
        };
        return RequestTokenAsync(form, signal);
    }

    public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private async Task<string> DiscoverAsync(CancellationToken signal)
    {
        JsonNode? discovery;
        try
        {
            discovery = await OAuthHttp.GetJsonAsync($"{_gateway}/v1/oauth", signal);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Could not load Radius OAuth config from {_gateway}: {exception.Message}");
        }

        return discovery?["authorizationEndpoint"]?.GetValue<string>() is { Length: > 0 } endpoint
            ? endpoint
            : throw new InvalidOperationException($"Invalid Radius OAuth config from {_gateway}");
    }

    private async Task<(string DeviceCode, string UserCode, string VerificationUri, int? Interval, int ExpiresIn)>
        RequestDeviceAuthorizationAsync(CancellationToken signal)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        };
        string response;
        try
        {
            response = await OAuthHttp.PostFormAsync($"{_gateway}/v1/oauth/device", form, signal);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"Radius OAuth device authorization failed. status=400; url={exception.Message}", exception);
        }

        var data = JsonNode.Parse(response)
            ?? throw new InvalidOperationException("Radius OAuth device authorization response is missing required fields");
        var deviceCode = data["device_code"]?.GetValue<string>();
        var userCode = data["user_code"]?.GetValue<string>();
        var verificationUri = data["verification_uri"]?.GetValue<string>();
        var expiresIn = data["expires_in"]?.GetValue<int>();
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode)
            || string.IsNullOrEmpty(verificationUri) || expiresIn is null)
        {
            throw new InvalidOperationException("Radius OAuth device authorization response is missing required fields");
        }

        return (deviceCode!, userCode!, verificationUri!, data["interval"]?.GetValue<int>(), expiresIn.Value);
    }

    private async Task<OAuthCredential> RequestTokenAsync(
        IReadOnlyDictionary<string, string> form, CancellationToken signal)
    {
        string response;
        try
        {
            response = await OAuthHttp.PostFormAsync($"{_gateway}/v1/oauth/token", form, signal);
        }
        catch (InvalidOperationException exception)
        {
            throw new OAuthTokenRequestException(
                $"Radius OAuth token request failed: {exception.Message}", exception);
        }

        var data = JsonNode.Parse(response)
            ?? throw new InvalidOperationException("Radius OAuth token response is invalid JSON");
        var access = data["access_token"]?.GetValue<string>();
        var refresh = data["refresh_token"]?.GetValue<string>();
        var expiresIn = data["expires_in"]?.GetValue<double>();
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || expiresIn is null)
        {
            throw new InvalidOperationException("Radius OAuth token response is missing required fields");
        }

        return new OAuthCredential
        {
            Access = access,
            Refresh = refresh,
            Expires = (long)(DateTime.UtcNow.AddSeconds(expiresIn.Value)
                .Subtract(TimeSpan.FromMilliseconds(TokenExpirySkewMs))
                - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
            Scope = data["scope"]?.GetValue<string>(),
        };
    }

    /// <summary>A token request failure carrying the OAuth error code.</summary>
    internal sealed class OAuthTokenRequestException : InvalidOperationException
    {
        public OAuthTokenRequestException(string message, string? oauthError) : base(message)
        {
            OauthError = oauthError ?? string.Empty;
        }

        public OAuthTokenRequestException(string message, Exception inner) : base(message, inner)
        {
            OauthError = ParseOauthError(message);
        }

        public string OauthError { get; }

        private static string ParseOauthError(string message)
        {
            var marker = "error=";
            var index = message.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return string.Empty;
            }

            var rest = message[(index + marker.Length)..];
            var end = rest.IndexOfAny(new[] { ';', ' ', '\n' });
            return end < 0 ? rest : rest[..end];
        }
    }
}
