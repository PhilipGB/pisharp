using PiSharp.Core.Models;

namespace PiSharp.Core.Models.Auth;

/// <summary>
/// Overrides for auth resolution (pinned pi-ai: AuthResolutionOverrides).
/// </summary>
public sealed record AuthResolutionOverrides
{
    /// <summary>Explicit API key, e.g. from --api-key. Wins over stored credentials.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Minimum remaining OAuth token validity required after refresh.</summary>
    public int? MinOAuthValidityMs { get; init; }
}

/// <summary>
/// Provider auth resolution (pinned pi-ai: resolveProviderAuth). A stored credential
/// owns the provider: ambient/env is consulted only when nothing is stored. There is no
/// silent env fallback after a failed refresh or for a credential type without a
/// matching handler.
/// </summary>
public static class CredentialResolver
{
    private const int DefaultOAuthMinimumValidityMs = 5 * 60 * 1000;
    private const int DefaultOAuthRefreshTimeoutMs = 15_000;

    /// <summary>
    /// Resolves request auth for a provider. Returns null when the provider is not
    /// configured (no credential and no ambient key).
    /// </summary>
    public static async Task<AuthResult?> ResolveProviderAuthAsync(
        IAuthProvidingProvider provider,
        ICredentialStore credentials,
        AuthContext authContext,
        AuthResolutionOverrides? overrides,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var auth = provider.Auth;

        if (overrides?.ApiKey is { } explicitKey && auth.ApiKey is not null)
        {
            return await ResolveApiKeyAsync(
                authContext,
                auth.ApiKey,
                provider.Id,
                new ApiKeyCredential(explicitKey),
                cancellationToken);
        }

        Credential? stored;
        try
        {
            stored = await credentials.ReadAsync(provider.Id, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new ModelsException(ModelsErrorCode.Auth, $"Credential store read failed for {provider.Id}", exception);
        }

        if (stored is not null)
        {
            if (stored is OAuthCredential oauth && auth.OAuth is { } oauthAuth)
            {
                return await ResolveStoredOAuthAsync(credentials, provider.Id, oauthAuth, oauth, overrides, cancellationToken);
            }

            if (stored is ApiKeyCredential apiKey && auth.ApiKey is { } apiKeyAuth)
            {
                return await ResolveApiKeyAsync(authContext, apiKeyAuth, provider.Id, apiKey, cancellationToken);
            }

            // A stored credential without a matching handler owns the provider: no env fallback.
            return null;
        }

        // Ambient (env vars, ADC files).
        return auth.ApiKey is { } ambient
            ? await ResolveApiKeyAsync(authContext, ambient, provider.Id, null, cancellationToken)
            : null;
    }

    /// <summary>
    /// OAuth resolution with double-checked locking (pinned pi-ai: resolveStoredOAuth):
    /// tokens with less than five minutes remaining refresh once under the store lock,
    /// re-checking expiry there, and persist the rotated credential before release.
    /// </summary>
    private static async Task<AuthResult?> ResolveStoredOAuthAsync(
        ICredentialStore credentials,
        string providerId,
        OAuthAuth oauth,
        OAuthCredential stored,
        AuthResolutionOverrides? overrides,
        CancellationToken cancellationToken)
    {
        var minimumValidityMs = Math.Max(
            DefaultOAuthMinimumValidityMs,
            overrides?.MinOAuthValidityMs is { } requested ? requested : 0);

        bool expiresSoon(OAuthCredential credential) =>
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + minimumValidityMs >= credential.Expires;

        var credential = stored;
        if (expiresSoon(credential))
        {
            // Optimistic check said expired; the authoritative check runs under the lock.
            Credential? post;
            try
            {
                var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                refreshTimeout.CancelAfter(DefaultOAuthRefreshTimeoutMs);
                try
                {
                    post = await credentials.ModifyAsync(
                        providerId,
                        async current =>
                        {
                            if (current is not OAuthCredential currentOauth)
                            {
                                return null; // logged out meanwhile
                            }

                            if (!expiresSoon(currentOauth))
                            {
                                return null; // another process/request refreshed
                            }

                            try
                            {
                                return await oauth.RefreshAsync(currentOauth, refreshTimeout.Token);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                throw new ModelsException(
                                    ModelsErrorCode.Oauth,
                                    $"OAuth refresh failed for {providerId}",
                                    exception);
                            }
                        },
                        cancellationToken);
                }
                finally
                {
                    refreshTimeout.Dispose();
                }
            }
            catch (ModelsException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ModelsException(
                    ModelsErrorCode.Auth,
                    $"Credential store modify failed for {providerId}",
                    exception);
            }

            if (post is not OAuthCredential refreshed)
            {
                return null; // logged out meanwhile
            }

            credential = refreshed;
            // The normal five-minute window triggers a refresh but does not impose a
            // provider contract. Explicit callers (such as bearer-token export) do
            // require the requested minimum after the refresh.
            if (overrides?.MinOAuthValidityMs is not null && expiresSoon(credential))
            {
                throw new ModelsException(
                    ModelsErrorCode.Oauth,
                    $"OAuth refresh returned a token that expires too soon for {providerId}");
            }
        }

        try
        {
            return new AuthResult
            {
                Auth = await oauth.ToAuthAsync(credential),
                Source = "OAuth",
            };
        }
        catch (Exception exception)
        {
            throw new ModelsException(ModelsErrorCode.Oauth, $"OAuth auth derivation failed for {providerId}", exception);
        }
    }

    private static async Task<AuthResult?> ResolveApiKeyAsync(
        AuthContext authContext,
        ApiKeyAuth apiKey,
        string providerId,
        ApiKeyCredential? credential,
        CancellationToken cancellationToken)
    {
        try
        {
            return await apiKey.ResolveAsync(
                new ApiKeyAuthInput
                {
                    Context = authContext,
                    Credential = credential,
                    CancellationToken = cancellationToken,
                });
        }
        catch (Exception exception)
        {
            throw new ModelsException(ModelsErrorCode.Auth, $"API key auth failed for provider {providerId}", exception);
        }
    }
}

/// <summary>
/// The provider surface the auth resolver needs (id + declared auth). Implemented by
/// provider registry specs so resolution never depends on stream implementations.
/// </summary>
public interface IAuthProvidingProvider
{
    /// <summary>Provider id.</summary>
    string Id { get; }

    /// <summary>Declared auth methods.</summary>
    ProviderAuth Auth { get; }
}
