using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.OAuth;

/// <summary>
/// OpenRouter OAuth flow (pinned pi-ai: auth/oauth/openrouter.ts). PKCE browser login
/// with a dynamic /oauth/callback/{uuid} path; the token endpoint mints an API key
/// (access = key) that never needs refreshing.
/// </summary>
public sealed class OpenRouterOAuth : OAuthAuth
{
    private const string AuthorizeUrl = "https://openrouter.ai/auth";
    private const string TokenUrl = "https://openrouter.ai/api/v1/auth/keys";
    private const int CallbackPort = 53693;
    private const int LoginTimeoutMs = 5 * 60 * 1000;
    private const int TokenExchangeTimeoutMs = 30_000;

    public override async Task<OAuthCredential> LoginAsync(IAuthInteraction interaction)
    {
        var pair = Pkce.Generate();
        var callbackPath = $"/oauth/callback/{Guid.NewGuid():N}";
        var redirectUri = $"http://localhost:{CallbackPort}{callbackPath}";

        await using var server = await CallbackServer.StartAsync(CallbackPort, callbackPath, string.Empty, "OpenRouter");
        var loginCts = CancellationTokenSource.CreateLinkedTokenSource(interaction.Signal);
        var loginTimeout = new CancellationTokenSource(LoginTimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(loginCts.Token, loginTimeout.Token);

        var manualCts = new CancellationTokenSource();
        using var abortRegistration = linked.Token.Register(() =>
        {
            server.CancelWait();
            manualCts.Cancel();
        });

        var manualTask = interaction.PromptAsync(
            new ManualCodePromptStep(
                "Complete login in your browser, or paste the authorization code / redirect URL here:",
                redirectUri),
            manualCts.Token);

        var authorize = string.Join('&',
        [
            "code=true",
            "response_type=code",
            $"client_id={Uri.EscapeDataString("openrouter-codex")}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"code_challenge={Uri.EscapeDataString(pair.Challenge)}",
            "code_challenge_method=S256",
        ]);

        interaction.Notify(new AuthEvent.AuthUrlEvent(
            $"{AuthorizeUrl}?{authorize}",
            "Complete login in your browser. If the browser is on another machine, paste the final redirect URL here."));

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
                code = AuthorizationInputParser.Parse(manualInput).Code;
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

        interaction.Notify(new AuthEvent.ProgressEvent("Exchanging authorization code for tokens..."));
        var key = await ExchangeAsync(code, pair.Verifier, linked.Token);
        manualCts.Cancel();
        return new OAuthCredential
        {
            Access = key,
            Refresh = string.Empty,
            Expires = long.MaxValue,
        };
    }

    private static async Task<string> ExchangeAsync(string code, string verifier, CancellationToken signal)
    {
        var body = new JsonObject
        {
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["code_challenge_method"] = "S256",
        }.ToJsonString();
        string responseText;
        try
        {
            responseText = await OAuthHttp.PostJsonAsync(TokenUrl, body, signal, TokenExchangeTimeoutMs);
        }
        catch (InvalidOperationException) when (signal.IsCancellationRequested)
        {
            throw new InvalidOperationException("Login cancelled");
        }

        var data = JsonNode.Parse(responseText);
        if (data?["key"]?.GetValue<string>() is { Length: > 0 } key)
        {
            return key;
        }

        var message = data?["error"]?["message"]?.GetValue<string>();
        throw new InvalidOperationException(
            message is null
                ? "OpenRouter OAuth token exchange failed"
                : $"OpenRouter OAuth token exchange failed: {message}");
    }

    public override Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken signal)
        => Task.FromResult(credential);

    public override Task<ModelAuth> ToAuthAsync(OAuthCredential credential)
        => Task.FromResult(new ModelAuth { ApiKey = credential.Access });
}
