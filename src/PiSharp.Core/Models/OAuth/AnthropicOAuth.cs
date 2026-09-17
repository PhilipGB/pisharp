using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// Anthropic Claude Pro/Max OAuth flow (pinned pi-ai: auth/oauth/anthropic.ts).
/// PKCE browser login with a 127.0.0.1:53692 callback, or a pasted redirect URL/code,
/// token exchange at platform.claude.com, and refresh via grant_type=refresh_token.
/// </summary>
public sealed class AnthropicOAuth : OAuthAuth
{
    private static readonly string ClientId =
        Encoding.UTF8.GetString(Convert.FromBase64String("OWQxYzI1MGEtZTYxYi00NGQ5LTg4ZWQtNTk0NGQxOTYyZjVl"));

    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const int CallbackPort = 53692;
    private const string CallbackPath = "/callback";
    private const string Scopes =
        "org:create_api_key user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload";
    private const int ExpirySkewMs = 5 * 60 * 1000;

    public override async Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
    {
        var pair = Pkce.Generate();
        var redirectUri = $"http://localhost:{CallbackPort}{CallbackPath}";

        await using var server = await CallbackServer.StartAsync(CallbackPort, CallbackPath, pair.Verifier, "Anthropic");

        var manualCts = new CancellationTokenSource();
        var abortRegistration = interaction.Signal.Register(() =>
        {
            server.CancelWait();
            manualCts.Cancel();
        });

        var manualTask = interaction.PromptAsync(
            new ManualCodePromptStep(
                "Complete login in your browser, or paste the authorization code / redirect URL here:",
                redirectUri),
            manualCts.Token);

        var authParams = string.Join('&',
        [
            "code=true",
            $"client_id={Uri.EscapeDataString(ClientId)}",
            "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"scope={Uri.EscapeDataString(Scopes)}",
            $"code_challenge={Uri.EscapeDataString(pair.Challenge)}",
            "code_challenge_method=S256",
            $"state={Uri.EscapeDataString(pair.Verifier)}",
        ]);

        interaction.Notify(new AuthEvent.AuthUrlEvent(
            $"{AuthorizeUrl}?{authParams}",
            "Complete login in your browser. If the browser is on another machine, paste the final redirect URL here."));

        string? code = null;
        var state = pair.Verifier;

        var browserResult = await server.WaitForCodeAsync();
        if (browserResult is { } result && result.Code.Length > 0)
        {
            code = result.Code;
            state = result.State;
        }
        else
        {
            string? manualInput = null;
            try
            {
                manualInput = await manualTask;
            }
            catch (OperationCanceledException)
            {
                // Manual input aborted; fall through to the missing-code error.
            }

            if (manualInput is not null)
            {
                var parsed = AuthorizationInputParser.Parse(manualInput);
                if (parsed.State is { Length: > 0 } parsedState && parsedState != pair.Verifier)
                {
                    throw new InvalidOperationException("OAuth state mismatch");
                }

                code = parsed.Code;
                state = parsed.State ?? pair.Verifier;
            }
        }

        if (code is null)
        {
            throw new InvalidOperationException("Missing authorization code");
        }

        interaction.Notify(new AuthEvent.ProgressEvent("Exchanging authorization code for tokens..."));
        var credential = await ExchangeAuthorizationCodeAsync(code, state, pair.Verifier, redirectUri, interaction.Signal);
        abortRegistration.Dispose();
        manualCts.Cancel();
        return credential;
    }

    public override async Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken signal)
    {
        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = credential.Refresh,
        }.ToJsonString();
        var response = await OAuthHttp.PostJsonAsync(TokenUrl, body, signal);
        var data = JsonNode.Parse(response)
            ?? throw new InvalidOperationException($"Anthropic token refresh returned invalid JSON. url={TokenUrl}; body={response}");

        return new OAuthCredential
        {
            Access = RequiredString(data, "access_token", response),
            Refresh = RequiredString(data, "refresh_token", response),
            Expires = EpochMsNow() + (long)(RequiredNumber(data, "expires_in", response) * 1000) - ExpirySkewMs,
            Scope = data["scope"]?.GetValue<string>(),
        };
    }

    public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });

    private static async Task<OAuthCredential> ExchangeAuthorizationCodeAsync(
        string code, string state, string verifier, string redirectUri, CancellationToken signal)
    {
        var body = new JsonObject
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["state"] = state,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }.ToJsonString();
        var response = await OAuthHttp.PostJsonAsync(TokenUrl, body, signal);
        var data = JsonNode.Parse(response)
            ?? throw new InvalidOperationException($"Token exchange returned invalid JSON. url={TokenUrl}; body={response}");

        return new OAuthCredential
        {
            Access = RequiredString(data, "access_token", response),
            Refresh = RequiredString(data, "refresh_token", response),
            Expires = EpochMsNow() + (long)(RequiredNumber(data, "expires_in", response) * 1000) - ExpirySkewMs,
        };
    }

    internal static long EpochMsNow()
        => (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

    internal static string RequiredString(JsonNode node, string name, string body)
        => node[name]?.GetValue<string>()
            is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(MissingFieldMessage(name, body));

    internal static double RequiredNumber(JsonNode node, string name, string body)
        => node[name]?.GetValue<double>()
            is { } value
            ? value
            : throw new InvalidOperationException(MissingFieldMessage(name, body));

    private static string MissingFieldMessage(string name, string body)
    {
        return "Token response missing field " + name + ". url=" + TokenUrl + "; body=" + body;
    }
}
