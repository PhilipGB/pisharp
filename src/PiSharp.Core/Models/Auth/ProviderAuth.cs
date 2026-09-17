namespace PiSharp.Core.Models.Auth;

/// <summary>
/// Input to api-key availability checks and resolution (pinned pi-ai: ApiKeyAuth
/// check/resolve input).
/// </summary>
public sealed record ApiKeyAuthInput
{
    public required AuthContext Context { get; init; }
    public ApiKeyCredential? Credential { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Api-key auth method for a provider (pinned pi-ai: ApiKeyAuth). A stored key plus
/// ambient sources (env vars, ADC files). Absent login means ambient-only.
/// </summary>
public abstract class ApiKeyAuth
{
    /// <summary>Display name, e.g. "Anthropic API key".</summary>
    public required string Name { get; init; }

    /// <summary>Interactive setup; null means ambient-only (no login prompt).</summary>
    public Func<IAuthInteraction, Task<ApiKeyCredential>>? Login { get; init; }

    /// <summary>
    /// Optional side-effect-free availability check. Null means availability is checked
    /// by resolving auth.
    /// </summary>
    public Func<ApiKeyAuthInput, Task<AuthCheck?>>? Check { get; init; }

    /// <summary>
    /// Resolves auth from the stored credential and/or ambient sources, merging per
    /// field. Null result means not configured.
    /// </summary>
    public abstract Task<AuthResult?> ResolveAsync(ApiKeyAuthInput input);

    /// <summary>
    /// Standard login prompt: a secret prompt that stores the entered key
    /// (pinned Pi's default for env-var providers).
    /// </summary>
    protected static Task<ApiKeyCredential> PromptForKeyAsync(IAuthInteraction interaction, string message)
    {
        var task = Task.Run(async () =>
        {
            interaction.Signal.ThrowIfCancellationRequested();
            var key = await interaction.PromptAsync(new SecretPromptStep(message), interaction.Signal);
            interaction.Signal.ThrowIfCancellationRequested();
            return new ApiKeyCredential(key);
        }, interaction.Signal);
        return task;
    }
}

/// <summary>
/// Standard ambient api-key auth: stored key wins, then environment variables in the
/// given order. Matches the dominant pinned Pi provider pattern (e.g. OpenAI, OpenRouter,
/// Groq, Cerebras).
/// </summary>
public sealed class EnvApiKeyAuth : ApiKeyAuth
{
    /// <summary>Environment variables consulted in order when no key is stored.</summary>
    public required IReadOnlyList<string> EnvironmentVariableNames { get; init; }

    /// <summary>
    /// When set, this variable resolves to an Authorization bearer header instead of an
    /// API key (e.g. ANTHROPIC_AUTH_TOKEN). Consulted before <see cref="EnvironmentVariableNames"/>.
    /// </summary>
    public string? BearerTokenEnvironmentVariable { get; init; }

    /// <summary>Message for the login prompt; null means ambient-only.</summary>
    public string? LoginMessage { get; init; }

    public override Task<AuthResult?> ResolveAsync(ApiKeyAuthInput input)
    {
        var ct = input.CancellationToken;
        var task = Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            if (input.Credential is { Key.Length: > 0 } credential)
            {
                return new AuthResult
                {
                    Auth = new ModelAuth { ApiKey = credential.Key },
                    Source = "stored credential",
                };
            }

            if (BearerTokenEnvironmentVariable is { } bearerVar)
            {
                var token = await input.Context.Env(bearerVar);
                ct.ThrowIfCancellationRequested();
                if (token is not null)
                {
                    return new AuthResult
                    {
                        Auth = new ModelAuth
                        {
                            Headers = new Dictionary<string, string>
                            {
                                ["Authorization"] = $"Bearer {token}",
                            },
                        },
                        Source = bearerVar,
                    };
                }
            }

            foreach (var envName in EnvironmentVariableNames)
            {
                var apiKey = await input.Context.Env(envName);
                ct.ThrowIfCancellationRequested();
                if (apiKey is not null)
                {
                    return new AuthResult { Auth = new ModelAuth { ApiKey = apiKey }, Source = envName };
                }
            }

            return null;
        }, ct);
        return task;
    }
}

/// <summary>
/// OAuth auth method for a provider (pinned pi-ai: OAuthAuth). The refresh/toAuth split
/// lets the runtime own the locked refresh pattern: RefreshAsync produces a credential,
/// ToAuthAsync derives request auth from whatever credential ends up stored.
/// </summary>
public abstract class OAuthAuth
{
    /// <summary>Display name, e.g. "Anthropic (Claude Pro/Max)".</summary>
    public required string Name { get; init; }

    /// <summary>Whether access is backed by a provider subscription.</summary>
    public bool IsSubscription { get; init; }

    /// <summary>Selector label for the OAuth login option.</summary>
    public string? LoginLabel { get; init; }

    /// <summary>Runs the login flow and returns the credential to persist.</summary>
    public abstract Task<OAuthCredential> LoginAsync(IAuthInteraction interaction);

    /// <summary>
    /// Exchanges the refresh token. Network call; throws on failure. The runtime runs
    /// this under the credential store lock.
    /// </summary>
    public abstract Task<OAuthCredential> RefreshAsync(OAuthCredential credential, CancellationToken cancellationToken);

    /// <summary>
    /// Side-effect-free derivation of request auth from a valid credential. Covers
    /// per-credential baseUrl (GitHub Copilot).
    /// </summary>
    public abstract Task<ModelAuth> ToAuthAsync(OAuthCredential credential);
}

/// <summary>
/// Provider auth: at least one of ApiKey/OAuth. Every provider has auth semantics —
/// even ambient-only and keyless local providers provide ApiKey auth whose resolution
/// reports whether the provider is configured (pinned pi-ai: ProviderAuth).
/// </summary>
public sealed class ProviderAuth
{
    public ApiKeyAuth? ApiKey { get; init; }
    public OAuthAuth? OAuth { get; init; }

    /// <summary>Creates a provider auth; at least one method must be present.</summary>
    public ProviderAuth()
    {
        if (ApiKey is null && OAuth is null)
        {
            throw new ArgumentException("A provider must declare at least one of ApiKey/OAuth auth.", nameof(ApiKey));
        }
    }
}
