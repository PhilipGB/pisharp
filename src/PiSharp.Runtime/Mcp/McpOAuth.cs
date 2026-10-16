using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Authentication;

namespace PiSharp.Runtime.Mcp;

/// <summary>OAuth settings for an HTTP MCP server. The SDK owns PKCE, discovery and refresh.</summary>
public sealed record McpOAuthSettings(string? ClientId, string? ClientSecret, Uri? CallbackUrl,
    IReadOnlyList<string> Scopes)
{
    public string? ClientName { get; init; }
    public Uri? AuthServerMetadataUrl { get; init; }
    public string ClientRegistration { get; init; } = "dcr";

    public static McpOAuthSettings Parse(JsonElement value)
    {
        string? Read(string name)
        {
            if (!value.TryGetProperty(name, out var property)) return null;
            if (property.ValueKind != JsonValueKind.String)
                throw new ArgumentException("MCP oauth." + name + " must be a string.");
            return property.GetString();
        }

        var known = new[] { "clientId", "clientSecret", "callbackUrl", "scope", "clientName",
            "authServerMetadataUrl", "clientRegistration" };
        if (value.EnumerateObject().Any(property => !known.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException("MCP OAuth has an unknown option.");
        var clientName = Read("clientName");
        if (clientName is not null && string.IsNullOrWhiteSpace(clientName))
            throw new ArgumentException("MCP oauth.clientName must be a non-empty string.");
        var metadataValue = Read("authServerMetadataUrl");
        Uri? metadataUrl = null;
        if (metadataValue is not null)
        {
            if (!Uri.TryCreate(metadataValue, UriKind.Absolute, out metadataUrl) ||
                metadataUrl.Scheme != Uri.UriSchemeHttps &&
                (metadataUrl.Scheme != Uri.UriSchemeHttp || !IsMetadataLoopback(metadataUrl)))
                throw new ArgumentException(
                    "MCP oauth.authServerMetadataUrl must be an https URL, or http on localhost, 127.0.0.1, or [::1].");
        }
        var clientRegistration = Read("clientRegistration") ?? "dcr";
        if (clientRegistration is not ("dcr" or "cimd"))
            throw new ArgumentException("MCP oauth.clientRegistration must be \"dcr\" or \"cimd\".");
        var callback = Read("callbackUrl");
        Uri? redirect = null;
        if (callback is not null)
        {
            if (!Uri.TryCreate(callback, UriKind.Absolute, out redirect))
                throw new ArgumentException("MCP OAuth callbackUrl must be a loopback HTTP URL with a port.");
            var callbackHost = redirect.Host.Trim('[', ']');
            if (redirect.Scheme != "http" || callbackHost is not ("127.0.0.1" or "localhost" or "::1") || redirect.IsDefaultPort ||
                !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment))
                throw new ArgumentException("MCP OAuth callbackUrl must be a loopback HTTP URL with a port.");
        }
        if (clientRegistration == "cimd")
        {
            if (Read("clientId") is not null || clientName is not null)
                throw new ArgumentException("MCP OAuth clientRegistration cimd cannot be combined with clientId or clientName.");
            if (redirect?.Host.Trim('[', ']') == "::1")
                throw new ArgumentException("MCP OAuth callbackUrl with clientRegistration cimd must use localhost or 127.0.0.1.");
            if (redirect is not null && redirect.AbsolutePath != "/callback")
                throw new ArgumentException("MCP OAuth callbackUrl must use the /callback path with clientRegistration cimd.");
        }
        return new McpOAuthSettings(Read("clientId"), Read("clientSecret"), redirect,
            (Read("scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ClientName = clientName,
            AuthServerMetadataUrl = metadataUrl,
            ClientRegistration = clientRegistration
        };
    }

    private static bool IsMetadataLoopback(Uri value)
    {
        var host = value.Host.Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.Ordinal) || host.Equals("::1", StringComparison.Ordinal);
    }

    public ClientOAuthOptions CreateOptions(Uri serverUrl, ITokenCache cache, Uri redirectUri,
        Func<AuthorizationCallbackContext, CancellationToken, Task<AuthorizationResult?>> callback,
        Uri? clientMetadataDocumentUri = null)
    {
        return new ClientOAuthOptions
        {
            RedirectUri = redirectUri,
            ClientId = ClientId,
            ClientSecret = ClientSecret is null ? null : Regex.Replace(ClientSecret,
                @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", match =>
                    Environment.GetEnvironmentVariable(match.Groups[1].Value) ??
                    throw new InvalidOperationException("Missing MCP OAuth secret environment variable.")),
            ClientMetadataDocumentUri = clientMetadataDocumentUri,
            DynamicClientRegistration = new DynamicClientRegistrationOptions
            {
                ClientName = ClientName ?? "pi"
            },
            Scopes = Scopes,
            TokenCache = cache,
            AuthorizationCallbackHandler = callback
        };
    }
}

public sealed class McpSignInRequiredException : Exception
{
    public McpSignInRequiredException(string? provider = null, string? serverName = null)
        : base(provider is null ? "MCP server requires explicit sign-in." :
            $"MCP server \"{serverName}\" requires sign-in. Run /login {provider} to sign in.") =>
        Provider = provider;

    public string? Provider { get; }
}
