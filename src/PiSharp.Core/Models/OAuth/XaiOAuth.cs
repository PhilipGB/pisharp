using System.Text.Json.Nodes;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// xAI OAuth (pinned pi-ai: auth/oauth/xai.ts). Device code flow against
/// auth.x.ai with refresh skew and the RFC 8628 slow_down handling.
/// </summary>
public sealed class XaiOAuth : OAuthAuth
{
    private const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    private const string Scope = "openid profile email offline_access grok-cli:access api:access";
    private const string DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code";
    private const string TokenUrl = "https://auth.x.ai/oauth2/token";
    private const int RefreshSkewMs = 5 * 60 * 1000;
    private const double DefaultTokenLifetimeSeconds = 3600;

    public override async Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        };
        var response = await OAuthHttp.PostFormAsync(DeviceCodeUrl, form, interaction.Signal);
        var json = JsonNode.Parse(response)
            ?? throw new InvalidOperationException($"Invalid xAI device code response: {response}");

        var deviceCode = json["device_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invalid xAI device code response: {response}");
        var userCode = json["user_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invalid xAI device code response: {response}");
        var interval = json["interval"]?.GetValue<int>();
        var verificationUri = json["verification_uri_complete"]?.GetValue<string>()
            ?? json["verification_uri"]?.GetValue<string>()
            ?? "https://auth.x.ai/activate";
        var expiresIn = json["expires_in"]?.GetValue<int>();

        interaction.Notify(new AuthEvent.DeviceCodeEvent(
            userCode!, verificationUri, interval, expiresIn));

        var credential = await DeviceCodePoller.PollAsync<OAuthCredential>(
            async () =>
            {
                var pollForm = new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["device_code"] = deviceCode,
                    ["client_id"] = ClientId,
                };
                try
                {
                    var pollResponse = await OAuthHttp.PostFormAsync(TokenUrl, pollForm, interaction.Signal);
                    return new DeviceCodePollResult.Complete<OAuthCredential>(
                        CredentialFromTokenResponse(pollResponse, null));
                }
                catch (InvalidOperationException exception)
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
            interval,
            expiresIn,
            waitBeforeFirstPoll: true,
            interaction.Signal);

        return credential;
    }

    public override async Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken signal)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.Refresh,
            ["client_id"] = ClientId,
        };
        var response = await OAuthHttp.PostFormAsync(TokenUrl, form, signal);
        return CredentialFromTokenResponse(response, credential.Scope);
    }

    private static OAuthCredential CredentialFromTokenResponse(string response, string? scope)
    {
        var json = JsonNode.Parse(response)
            ?? throw new InvalidOperationException($"Invalid xAI token response: {response}");
        var access = json["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invalid xAI token response: {response}");
        var refresh = json["refresh_token"]?.GetValue<string>() ?? string.Empty;
        var expiresIn = json["expires_in"]?.GetValue<double>() ?? DefaultTokenLifetimeSeconds;

        return new OAuthCredential
        {
            Access = access,
            Refresh = refresh,
            Expires = (long)(DateTime.UtcNow.AddSeconds(expiresIn).Subtract(TimeSpan.FromMilliseconds(RefreshSkewMs))
                - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
            Scope = scope ?? Scope,
        };
    }

    public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
}
