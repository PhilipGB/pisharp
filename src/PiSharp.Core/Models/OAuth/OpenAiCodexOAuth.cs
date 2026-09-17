using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// OpenAI Codex OAuth (pinned pi-ai: auth/oauth/openai-codex.ts). PKCE browser login
/// on localhost:1455/auth/callback, or a device-code fallback (headless). Token
/// responses are plain JSON (not JWT expiry parsing): access/refresh/expires_in.
/// </summary>
public sealed class OpenAiCodexOAuth : OAuthAuth
{
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string AuthBaseUrl = "https://auth.openai.com";
    private const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";
    private const string TokenUrl = "https://auth.openai.com/oauth/token";
    private const string RedirectUri = "http://localhost:1455/auth/callback";
    private const string DeviceUserCodeUrl = "https://auth.openai.com/api/accounts/deviceauth/usercode";
    private const string DeviceTokenUrl = "https://auth.openai.com/api/accounts/deviceauth/token";
    private const string DeviceVerificationUri = "https://auth.openai.com/codex/device";
    private const string DeviceRedirectUri = "https://auth.openai.com/deviceauth/callback";
    private const int DeviceCodeTimeoutSeconds = 15 * 60;
    private const string Scope = "openid profile email offline_access";

    public override async Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
    {
        var method = await interaction.PromptAsync(
            new SelectPromptStep(
                "Select OpenAI Codex login method:",
                [
                    new SelectableOption("browser", "Browser login (default)"),
                    new SelectableOption("device_code", "Device code login (headless)"),
                ]),
            interaction.Signal);

        return method switch
        {
            "device_code" => await LoginWithDeviceCodeAsync(interaction),
            "browser" => await LoginWithBrowserAsync(interaction),
            _ => throw new InvalidOperationException($"Unknown OpenAI Codex login method: {method}"),
        };
    }

    private async Task<OAuthCredential> LoginWithBrowserAsync(IAuthInteraction interaction)
    {
        var pair = Pkce.Generate();
        var state = OAuthState.Generate();
        var authorize = string.Join('&',
        [
            "response_type=code",
            $"client_id={ClientId}",
            $"redirect_uri={Uri.EscapeDataString(RedirectUri)}",
            $"scope={Uri.EscapeDataString(Scope)}",
            $"code_challenge={pair.Challenge}",
            "code_challenge_method=S256",
            $"state={Uri.EscapeDataString(state)}",
            "id_token_add_organizations=true",
            "codex_cli_simplified_flow=true",
            "originator=pi",
        ]);

        await using var server = await CallbackServer.StartAsync(1455, "/auth/callback", state, "OpenAI");
        var manualCts = new CancellationTokenSource();
        using var abortRegistration = interaction.Signal.Register(() =>
        {
            server.CancelWait();
            manualCts.Cancel();
        });

        var manualTask = interaction.PromptAsync(
            new ManualCodePromptStep(
                "Complete login in your browser, or paste the authorization code / redirect URL here:",
                RedirectUri),
            manualCts.Token);

        interaction.Notify(new AuthEvent.AuthUrlEvent(
            $"{AuthorizeUrl}?{authorize}",
            "A browser window should open. Complete login to finish."));

        string? code = null;
        var browserResult = await server.WaitForCodeAsync();
        if (browserResult is { } result)
        {
            code = result.Code;
        }
        else
        {
            try
            {
                var manualInput = await manualTask;
                var parsed = AuthorizationInputParser.Parse(manualInput);
                if (parsed.State is { Length: > 0 } parsedState && parsedState != state)
                {
                    throw new InvalidOperationException("State mismatch");
                }

                code = parsed.Code;
            }
            catch (OperationCanceledException)
            {
                // Manual input aborted.
            }
        }

        if (code is null)
        {
            throw new InvalidOperationException("Missing authorization code");
        }

        manualCts.Cancel();
        return await ExchangeCodeAsync(code, pair.Verifier, RedirectUri, interaction.Signal);
    }

    private async Task<OAuthCredential> LoginWithDeviceCodeAsync(IAuthInteraction interaction)
    {
        var deviceBody = new JsonObject { ["client_id"] = ClientId }.ToJsonString();
        var deviceResponse = await OAuthHttp.PostJsonAsync(DeviceUserCodeUrl, deviceBody, interaction.Signal);
        var deviceJson = JsonNode.Parse(deviceResponse)
            ?? throw new InvalidOperationException($"Invalid OpenAI Codex device code response: {deviceResponse}");
        var deviceAuthId = deviceJson["device_auth_id"]?.GetValue<string>();
        var userCode = deviceJson["user_code"]?.GetValue<string>();
        if (string.IsNullOrEmpty(deviceAuthId) || string.IsNullOrEmpty(userCode))
        {
            throw new InvalidOperationException($"Invalid OpenAI Codex device code response: {deviceResponse}");
        }

        var intervalSeconds = deviceJson["interval_seconds"]?.GetValue<int>();
        interaction.Notify(new AuthEvent.DeviceCodeEvent(
            userCode!, DeviceVerificationUri, intervalSeconds, DeviceCodeTimeoutSeconds));

        var success = await DeviceCodePoller.PollAsync<(string Code, string Verifier)>(
            async () =>
            {
                var pollBody = new JsonObject
                {
                    ["client_id"] = ClientId,
                    ["device_auth_id"] = deviceAuthId,
                    ["user_code"] = userCode,
                }.ToJsonString();
                try
                {
                    var pollResponse = await OAuthHttp.PostJsonAsync(DeviceTokenUrl, pollBody, interaction.Signal);
                    var pollJson = JsonNode.Parse(pollResponse)
                        ?? throw new InvalidOperationException($"Invalid OpenAI Codex device auth token response: {pollResponse}");
                    var authorizationCode = pollJson["authorization_code"]?.GetValue<string>();
                    var codeVerifier = pollJson["code_verifier"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(authorizationCode) || string.IsNullOrEmpty(codeVerifier))
                    {
                        throw new InvalidOperationException(
                            $"Invalid OpenAI Codex device auth token response: {pollResponse}");
                    }

                    return new DeviceCodePollResult.Complete<(string Code, string Verifier)>((authorizationCode, codeVerifier));
                }
                catch (InvalidOperationException exception)
                    when (exception.Message.Contains("authorization_pending", StringComparison.Ordinal)
                        || exception.Message.Contains("status=400", StringComparison.Ordinal))
                {
                    if (exception.Message.Contains("authorization_pending", StringComparison.Ordinal))
                    {
                        return new DeviceCodePollResult.Pending();
                    }

                    if (exception.Message.Contains("slow_down", StringComparison.Ordinal))
                    {
                        return new DeviceCodePollResult.SlowDown(null);
                    }

                    throw;
                }
            },
            intervalSeconds,
            DeviceCodeTimeoutSeconds,
            waitBeforeFirstPoll: true,
            interaction.Signal);

        return await ExchangeCodeAsync(success.Code, success.Verifier, DeviceRedirectUri, interaction.Signal);
    }

    private static async Task<OAuthCredential> ExchangeCodeAsync(
        string code, string verifier, string redirectUri, CancellationToken signal)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        };
        var response = await OAuthHttp.PostFormAsync(TokenUrl, form, signal);
        return CredentialsFromTokenResponse(response, "exchange");
    }

    public override async Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken signal)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.Refresh,
            ["client_id"] = ClientId,
        };
        string response;
        try
        {
            response = await OAuthHttp.PostFormAsync(TokenUrl, form, signal);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"OpenAI Codex token refresh error: {exception.Message}");
        }

        return CredentialsFromTokenResponse(response, "refresh");
    }

    private static OAuthCredential CredentialsFromTokenResponse(string response, string operation)
    {
        var json = JsonNode.Parse(response);
        var access = json?["access_token"]?.GetValue<string>();
        var refresh = json?["refresh_token"]?.GetValue<string>();
        var expiresIn = json?["expires_in"]?.GetValue<double>();
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh) || expiresIn is null)
        {
            throw new InvalidOperationException(
                $"OpenAI Codex token {operation} response missing fields: {response}");
        }

        return new OAuthCredential
        {
            Access = access,
            Refresh = refresh,
            Expires = (long)(DateTime.UtcNow.AddSeconds(expiresIn.Value)
                - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
        };
    }

    public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
}
