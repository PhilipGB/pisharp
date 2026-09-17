using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// GitHub Copilot OAuth (pinned pi-ai: auth/oauth/github-copilot.ts). Device code flow
/// against github.com or a GitHub Enterprise domain, exchanging the GitHub token for
/// a Copilot API token at /copilot_internal/v2/token. The request baseUrl is derived
/// from the token's proxy-ep. Model list fetch/enable is intentionally omitted in
/// PiSharp (documented in docs/PARITY.md).
/// </summary>
public sealed class GitHubCopilotOAuth : OAuthAuth
{
    private static readonly string ClientId =
        System.Text.Encoding.UTF8.GetString(Convert.FromBase64String("SXYxLmI1MDdhMDhjODdlY2ZlOTg="));

    private static readonly Dictionary<string, string> CopilotHeaders = new()
    {
        ["User-Agent"] = "GitHubCopilotChat/0.35.0",
        ["Editor-Version"] = "vscode/1.107.0",
        ["Editor-Plugin-Version"] = "copilot-chat/0.35.0",
        ["Copilot-Integration-Id"] = "vscode-chat",
    };

    public override async Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
    {
        var input = await interaction.PromptAsync(
            new TextPromptStep("GitHub Enterprise URL/domain (blank for github.com)", "company.ghe.com"),
            interaction.Signal);
        if (interaction.Signal.IsCancellationRequested)
        {
            throw new InvalidOperationException("Login cancelled");
        }

        var enterpriseDomain = NormalizeDomain(input);
        if (input.Trim().Length > 0 && enterpriseDomain is null)
        {
            throw new InvalidOperationException("Invalid GitHub Enterprise URL/domain");
        }

        var domain = enterpriseDomain ?? "github.com";

        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "read:user",
        };
        var deviceResponse = await OAuthHttp.PostFormAsync(
            $"https://{domain}/login/device/code", form, interaction.Signal,
            new Dictionary<string, string> { ["User-Agent"] = "GitHubCopilotChat/0.35.0" });
        var deviceJson = JsonNode.Parse(deviceResponse)
            ?? throw new InvalidOperationException("Invalid device code response");
        var deviceCode = deviceJson["device_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Invalid device code response");
        var userCode = deviceJson["user_code"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Invalid device code response");
        var interval = deviceJson["interval"]?.GetValue<int>();
        var expiresIn = deviceJson["expires_in"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Invalid device code response");
        var verificationUri = deviceJson["verification_uri"]?.GetValue<string>() ?? $"https://{domain}/login/device";

        interaction.Notify(new AuthEvent.DeviceCodeEvent(userCode!, verificationUri, interval, expiresIn));

        var githubAccessToken = await DeviceCodePoller.PollAsync<string>(
            async () =>
            {
                var pollForm = new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                };
                var pollResponse = await OAuthHttp.PostFormAsync(
                    $"https://{domain}/login/oauth/access_token", pollForm, interaction.Signal,
                    new Dictionary<string, string> { ["User-Agent"] = "GitHubCopilotChat/0.35.0" });
                var pollJson = JsonNode.Parse(pollResponse);
                if (pollJson?["access_token"]?.GetValue<string>() is { } token)
                {
                    return new DeviceCodePollResult.Complete<string>(token);
                }

                var error = pollJson?["error"]?.GetValue<string>();
                if (error == "authorization_pending")
                {
                    return new DeviceCodePollResult.Pending();
                }

                if (error == "slow_down")
                {
                    return new DeviceCodePollResult.SlowDown(pollJson?["interval"]?.GetValue<int>());
                }

                var description = pollJson?["error_description"]?.GetValue<string>();
                return new DeviceCodePollResult.Failed(
                    description is null ? $"Device flow failed: {error}" : $"Device flow failed: {error}: {description}");
            },
            interval,
            expiresIn,
            waitBeforeFirstPoll: true,
            interaction.Signal);

        var credential = await ExchangeCopilotTokenAsync(githubAccessToken, enterpriseDomain, interaction.Signal);
        return credential;
    }

    public override Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken signal)
        => ExchangeCopilotTokenAsync(credential.Refresh, EnterpriseDomainOf(credential), signal);

    public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
        => Task.FromResult(new ModelAuth
        {
            ApiKey = credential.Access,
            BaseUrl = GetBaseUrl(credential.Access, EnterpriseDomainOf(credential)),
        });

    private static async Task<OAuthCredential> ExchangeCopilotTokenAsync(
        string githubToken, string? enterpriseDomain, CancellationToken signal)
    {
        var domain = enterpriseDomain ?? "github.com";
        var headers = new Dictionary<string, string>(CopilotHeaders)
        {
            ["Authorization"] = $"Bearer {githubToken}",
        };
        JsonNode? raw;
        try
        {
            raw = await OAuthHttp.GetJsonAsync($"https://api.{domain}/copilot_internal/v2/token", signal, headers);
        }
        catch (InvalidOperationException) when (signal.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException($"Invalid Copilot token response: {exception.Message}");
        }

        var token = raw?["token"]?.GetValue<string>();
        var expiresAt = raw?["expires_at"]?.GetValue<double>();
        if (string.IsNullOrEmpty(token) || expiresAt is null)
        {
            throw new InvalidOperationException("Invalid Copilot token response fields");
        }

        var extra = enterpriseDomain is not null
            ? new Dictionary<string, object?> { ["enterpriseUrl"] = enterpriseDomain }
            : null;
        return new OAuthCredential
        {
            Access = token,
            Refresh = githubToken,
            Expires = (long)(expiresAt.Value * 1000) - 5 * 60 * 1000,
            Extra = extra,
        };
    }

    private static string? EnterpriseDomainOf(OAuthCredential credential)
        => credential.Extra?["enterpriseUrl"] as string;

    private static string? NormalizeDomain(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            var url = trimmed.Contains("://", StringComparison.Ordinal)
                ? new Uri(trimmed)
                : new Uri($"https://{trimmed}");
            return url.Host;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Derives the Copilot API base URL from the token's proxy-ep claim, falling back
    /// to the enterprise domain or the individual endpoint (pinned getGitHubCopilotBaseUrl).
    /// </summary>
    public static string GetBaseUrl(string? token, string? enterpriseDomain)
    {
        if (token is not null)
        {
            var match = Regex.Match(token, @"proxy-ep=([^;]+)");
            if (match.Success)
            {
                var proxyHost = match.Groups[1].Value;
                var apiHost = proxyHost.StartsWith("proxy.", StringComparison.Ordinal)
                    ? "api." + proxyHost["proxy.".Length..]
                    : proxyHost;
                return $"https://{apiHost}";
            }
        }

        if (enterpriseDomain is not null)
        {
            return $"https://copilot-api.{enterpriseDomain}";
        }

        return "https://api.individual.githubcopilot.com";
    }
}
