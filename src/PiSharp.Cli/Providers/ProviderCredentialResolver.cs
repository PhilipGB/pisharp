using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Resolves provider-owned credentials and binds explicit runtime credentials to one provider.</summary>
internal sealed class ProviderCredentialResolver(AuthStorage storage, ProviderOAuthCoordinator oauthCoordinator,
    Func<string, string?> getEnvironment, string? runtimeApiKey)
{
    private string? _runtimeApiKeyProvider;
    public string? BoundProvider => _runtimeApiKeyProvider;

    public void BindRuntimeCredential(string provider)
    {
        if (!string.IsNullOrWhiteSpace(runtimeApiKey))
            Interlocked.CompareExchange(ref _runtimeApiKeyProvider, provider, null);
    }

    public async Task<(string Key, bool Authenticated, string Source)> ResolveAsync(ProviderProfile provider,
        bool useRuntimeOverride = false, CancellationToken cancellationToken = default,
        bool allowOAuthRefresh = true)
    {
        if (provider.Id == "llama.cpp")
        {
            var llamaStored = await storage.ReadAsync(provider.Id, cancellationToken);
            return await ResolveAuthCoreAsync(provider, llamaStored, useRuntimeOverride, allowOAuthRefresh, cancellationToken);
        }
        if (useRuntimeOverride && !string.IsNullOrWhiteSpace(runtimeApiKey) &&
            (_runtimeApiKeyProvider is null || _runtimeApiKeyProvider == provider.Id))
            return (runtimeApiKey, true, "command line");
        var stored = await storage.ReadAsync(provider.Id, cancellationToken);
        return await ResolveAuthCoreAsync(provider, stored, useRuntimeOverride, allowOAuthRefresh, cancellationToken);
    }

    internal async Task<(string Key, bool Authenticated, string Source, IReadOnlyDictionary<string, string> Environment)> ResolveClassifierAsync(
        ProviderProfile provider, CancellationToken cancellationToken)
    {
        var stored = await storage.ReadAsync(provider.Id, cancellationToken);
        var auth = await ResolveAuthCoreAsync(provider, stored, _runtimeApiKeyProvider == provider.Id, true, cancellationToken);
        var environment = new Dictionary<string, string>();
        if (provider.Id == "cloudflare-workers-ai")
        {
            var name = "CLOUDFLARE_ACCOUNT_ID";
            var account = stored?.Type == "api_key" && stored.Env?.TryGetValue(name, out var value) == true
                ? value : getEnvironment(name);
            if (account is not null) environment[name] = account;
        }
        return (auth.Key, auth.Authenticated, auth.Source, environment);
    }

    private async Task<(string Key, bool Authenticated, string Source)> ResolveAuthCoreAsync(ProviderProfile provider,
        StoredCredential? stored, bool useRuntimeOverride, bool allowOAuthRefresh, CancellationToken cancellationToken)
    {
        if (provider.Id == "llama.cpp")
        {
            if (stored is not null && stored.Type != "api_key")
                return ("not-configured", false, "unsupported stored credential type");
            if (ResolveRouterUrl(stored) is null)
                return ("not-configured", false, "LLAMA_BASE_URL is not configured");
            if (useRuntimeOverride && !string.IsNullOrWhiteSpace(runtimeApiKey) &&
                (_runtimeApiKeyProvider is null || _runtimeApiKeyProvider == provider.Id))
                return (runtimeApiKey, true, "command line");
            if (stored?.Key is not null) return (stored.Key, true, "stored API key");
            if (getEnvironment("LLAMA_API_KEY") is { } environmentKey)
                return (environmentKey, true, "LLAMA_API_KEY");
            return ("local", true, "local llama.cpp router");
        }
        if (useRuntimeOverride && !string.IsNullOrWhiteSpace(runtimeApiKey) &&
            (_runtimeApiKeyProvider is null || _runtimeApiKeyProvider == provider.Id))
            return (runtimeApiKey, true, "command line");
        if (stored is not null)
        {
            // A stored credential owns the provider: never fall back to ambient credentials.
            if (stored.Type == "oauth")
            {
                if (!provider.OAuthSupported)
                    return ("not-configured", false, "OAuth unsupported for provider");
                if (!oauthCoordinator.Supports(provider.Id))
                    return ("not-configured", false, "OAuth adapter unavailable");
                return await oauthCoordinator.ResolveAsync(provider.Id, stored, allowOAuthRefresh, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (!provider.ApiKeySupported)
                return ("not-configured", false, "API key authentication unsupported for provider");
            return (stored.Key!, true, "stored API key");
        }
        if (!string.IsNullOrWhiteSpace(provider.ConfiguredApiKey)) return (provider.ConfiguredApiKey, true, "models.json");
        if (provider.Id == "anthropic")
        {
            var authToken = getEnvironment("ANTHROPIC_AUTH_TOKEN");
            if (!string.IsNullOrWhiteSpace(authToken)) return (authToken, true, "ANTHROPIC_AUTH_TOKEN");
            var oauthToken = getEnvironment("ANTHROPIC_OAUTH_TOKEN");
            if (!string.IsNullOrWhiteSpace(oauthToken)) return (oauthToken, true, "ANTHROPIC_OAUTH_TOKEN");
        }
        if (provider.ApiKeyEnvironment is not null && !string.IsNullOrWhiteSpace(getEnvironment(provider.ApiKeyEnvironment)))
            return (getEnvironment(provider.ApiKeyEnvironment)!, true, provider.ApiKeyEnvironment);
        if (provider.Id == "anthropic" && ResolveAnthropicWorkloadIdentity() is not null)
            return (string.Empty, true, "workload identity federation");
        if (provider.GoogleVertex?.HasConfiguredApplicationDefaultCredentials == true)
            return (GoogleVertexProviderOptions.AdcCredentialMarker, true, GoogleVertexProviderOptions.AdcAuthSource);
        if (provider.Bedrock?.AmbientAuthSource is { } bedrockAuthSource)
            return (string.Empty, true, bedrockAuthSource);
        return provider.AuthRequired ? ("not-configured", false, "authentication required") :
            ("not-needed", true, "not required");
    }

    public AnthropicWorkloadIdentityOptions? ResolveAnthropicWorkloadIdentity()
    {
        var ruleId = getEnvironment("ANTHROPIC_FEDERATION_RULE_ID");
        var organizationId = getEnvironment("ANTHROPIC_ORGANIZATION_ID");
        var tokenFile = getEnvironment("ANTHROPIC_IDENTITY_TOKEN_FILE");
        if (string.IsNullOrWhiteSpace(ruleId) || string.IsNullOrWhiteSpace(organizationId) ||
            string.IsNullOrWhiteSpace(tokenFile)) return null;
        return new AnthropicWorkloadIdentityOptions(ruleId, organizationId, tokenFile,
            NullIfWhiteSpace(getEnvironment("ANTHROPIC_SERVICE_ACCOUNT_ID")),
            NullIfWhiteSpace(getEnvironment("ANTHROPIC_WORKSPACE_ID")));
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public Uri? ResolveRouterUrl(StoredCredential? stored)
    {
        var value = stored?.Env?.TryGetValue("LLAMA_BASE_URL", out var storedUrl) == true &&
                    !string.IsNullOrWhiteSpace(storedUrl)
            ? storedUrl : getEnvironment("LLAMA_BASE_URL");
        return string.IsNullOrWhiteSpace(value) ? null : LlamaRouterClient.NormalizeServerUrl(value);
    }

}
