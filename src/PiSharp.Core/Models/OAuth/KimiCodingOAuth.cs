using System.Text.Json.Nodes;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// Kimi for Coding OAuth (pinned pi-ai: auth/oauth/kimi-coding.ts). Device code flow
/// against auth.kimi.com (overridable via KIMI_CODE_OAUTH_HOST / KIMI_OAUTH_HOST).
/// </summary>
public sealed class KimiCodingOAuth : OAuthAuth
{
    private const string ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";
    private const string DefaultOauthHost = "https://auth.kimi.com";
    private const int DeviceCodeTimeoutSeconds = 15 * 60;
    private const int DefaultPollIntervalSeconds = 5;
    private const int RequestTimeoutMs = 30 * 1000;

    private static string OauthHost()
    {
        return Environment.GetEnvironmentVariable("KIMI_CODE_OAUTH_HOST")
            ?? Environment.GetEnvironmentVariable("KIMI_OAUTH_HOST")
            ?? DefaultOauthHost;
    }

    public override async Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
    {
        var host = OauthHost();
        var form = new Dictionary<string, string> { ["client_id"] = ClientId };
        var response = await OAuthHttp.PostFormAsync(
            $"{host}/api/oauth/device_authorization", form, interaction.Signal, timeoutMs: RequestTimeoutMs);
        var json = JsonNode.Parse(response)
            ?? throw new InvalidOperationException($"Invalid Kimi device code response: {response}");

        var deviceCode = json["device_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invalid Kimi device code response: {response}");
        var userCode = json["user_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invalid Kimi device code response: {response}");
        var interval = json["interval"]?.GetValue<int>() ?? DefaultPollIntervalSeconds;
        var expiresIn = json["expires_in"]?.GetValue<int>() ?? DeviceCodeTimeoutSeconds;

        interaction.Notify(new AuthEvent.DeviceCodeEvent(userCode!, $"{host}/activate", interval, expiresIn));

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
                    var pollResponse = await OAuthHttp.PostFormAsync(
                        $"{host}/api/oauth/token", pollForm, interaction.Signal, timeoutMs: RequestTimeoutMs);
                    return new DeviceCodePollResult.Complete<OAuthCredential>(
                        CredentialFromTokenResponse(pollResponse));
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
        var host = OauthHost();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.Refresh,
            ["client_id"] = ClientId,
        };
        string response;
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                response = await OAuthHttp.PostFormAsync(
                    $"{host}/api/oauth/token", form, signal, timeoutMs: RequestTimeoutMs);
                break;
            }
            catch (InvalidOperationException) when (attempt < 3 && signal.IsCancellationRequested == false)
            {
                // Retry refresh (pinned REFRESH_MAX_RETRIES = 3).
            }
        }

        return CredentialFromTokenResponse(response);
    }

    private static OAuthCredential CredentialFromTokenResponse(string response)
    {
        var json = JsonNode.Parse(response)
            ?? throw new InvalidOperationException($"Invalid Kimi token response: {response}");
        var access = json["access_token"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invalid Kimi token response: {response}");
        var refresh = json["refresh_token"]?.GetValue<string>() ?? string.Empty;
        var expiresIn = json["expires_in"]?.GetValue<double>() ?? 3600;

        return new OAuthCredential
        {
            Access = access,
            Refresh = refresh,
            Expires = (long)(DateTime.UtcNow.AddSeconds(expiresIn)
                - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
        };
    }

    public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
        => Task.FromResult(new ModelAuth
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {credential.Access}" },
        });
}
