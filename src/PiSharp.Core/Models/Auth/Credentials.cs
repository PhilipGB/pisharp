namespace PiSharp.Core.Models.Auth;

/// <summary>
/// A stored credential for one provider (pinned pi-ai: Credential). Exactly one
/// type-tagged credential per provider in auth.json.
/// </summary>
public abstract record Credential
{
    /// <summary>Discriminator persisted in auth.json: "api_key" or "oauth".</summary>
    public abstract string Type { get; }
}

/// <summary>
/// Stored api-key credential (pinned pi-ai: ApiKeyCredential). Key optional for
/// ambient-only providers. Env holds provider-scoped config values persisted next to the
/// key (pinned: Cloudflare account/gateway ids, the llama.cpp server URL).
/// </summary>
public sealed record ApiKeyCredential(string? Key = null, IReadOnlyDictionary<string, string>? Env = null) : Credential
{
    public override string Type => "api_key";
}

/// <summary>
/// Stored canonical OAuth credential (pinned pi-ai: OAuthCredential). Expires is
/// epoch milliseconds. Provider-specific fields (clientId, proxyEndpoint, email, ...)
/// are preserved so login flows can rehydrate them on refresh.
/// </summary>
public sealed record OAuthCredential : Credential
{
    public override string Type => "oauth";

    public required string Access { get; init; }
    public required string Refresh { get; init; }
    public required long Expires { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? ApiKey { get; init; }
    public string? BaseUrl { get; init; }
    public string? ProxyEndpoint { get; init; }
    public string? Email { get; init; }
    public string? Scope { get; init; }
    public IReadOnlyDictionary<string, object?>? Extra { get; init; }
}

/// <summary>Non-secret credential metadata for account/status enumeration.</summary>
public sealed record CredentialInfo(string ProviderId, string Type);

/// <summary>
/// Request auth for a single model request (pinned pi-ai: ModelAuth). Anything that
/// cannot be expressed as apiKey/headers/baseUrl is provider config, not auth.
/// </summary>
public sealed record ModelAuth
{
    public string? ApiKey { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? BaseUrl { get; init; }
}

/// <summary>Result of resolving auth for a model request (pinned pi-ai: AuthResult).</summary>
public sealed record AuthResult
{
    public required ModelAuth Auth { get; init; }

    /// <summary>
    /// Provider-scoped environment/config values resolved from the credential and ambient
    /// context (pinned AuthResult.env); carried into refresh-credential reconstruction.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    public string? Source { get; init; }
}

/// <summary>
/// Environment access for auth resolution (pinned pi-ai: AuthContext). Injectable for
/// tests; the default reads process environment variables and checks file existence
/// with "~" expansion.
/// </summary>
public sealed record AuthContext
{
    public required Func<string, Task<string?>> Env { get; init; }
    public required Func<string, Task<bool>> FileExists { get; init; }

    /// <summary>Creates the default context reading from the process environment.</summary>
    public static AuthContext CreateDefault() => new()
    {
        Env = name => Task.FromResult(Environment.GetEnvironmentVariable(name)),
        FileExists = async path =>
        {
            var resolved = path;
            if (resolved.StartsWith("~", StringComparison.Ordinal))
            {
                resolved = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + resolved[1..];
            }

            try
            {
                await Task.Yield();
                return File.Exists(resolved);
            }
            catch
            {
                return false;
            }
        },
    };
}

/// <summary>Outcome of an availability check (pinned pi-ai: AuthCheck).</summary>
public sealed record AuthCheck(string? Source, string Type);
