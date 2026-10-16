using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Authentication;

namespace PiSharp.Runtime.Mcp;

/// <summary>Resolves Pi's opt-in CIMD identity before the MCP SDK snapshots OAuth options.</summary>
internal static class McpOAuthCimdDiscovery
{
    private const string s_sharedDocument = "https://pi.dev/oauth/client.json";

    public static async Task<McpOAuthCimdIdentity?> ResolveAsync(Uri serverUrl, McpOAuthSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.ClientRegistration != "cimd") return null;

        using var http = new HttpClient();
        using var challenge = await http.GetAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        var resourceMetadataUrl = ReadResourceMetadataUrl(challenge) ?? GetResourceMetadataUrl(serverUrl);
        using var resourceResponse = await http.GetAsync(resourceMetadataUrl, cancellationToken).ConfigureAwait(false);
        resourceResponse.EnsureSuccessStatusCode();
        using var resourceDocument = JsonDocument.Parse(await resourceResponse.Content
            .ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var authServer = ReadAuthorizationServer(resourceDocument.RootElement);

        using var authorizationDocument = await ReadAuthorizationMetadataAsync(http,
            settings.AuthServerMetadataUrl is { } metadataUrl ? [metadataUrl] :
                GetAuthorizationMetadataUrls(authServer), cancellationToken)
            .ConfigureAwait(false);
        var metadata = authorizationDocument.RootElement;
        if (!metadata.TryGetProperty("client_id_metadata_document_supported", out var cimdSupported) ||
            cimdSupported.ValueKind != JsonValueKind.True ||
            !metadata.TryGetProperty("token_endpoint_auth_methods_supported", out var methods) ||
            methods.ValueKind != JsonValueKind.Array || !methods.EnumerateArray().Any(method =>
                method.ValueKind == JsonValueKind.String && method.GetString() == "none"))
            throw new InvalidOperationException(
                "MCP authorization server does not advertise CIMD with token endpoint authentication method none.");

        var issuerResponses = metadata.TryGetProperty("authorization_response_iss_parameter_supported", out var issuerFlag) &&
            issuerFlag.ValueKind == JsonValueKind.True;
        var id = GetServerId(serverUrl);
        var documentUri = issuerResponses ? new Uri(s_sharedDocument) :
            new Uri("https://pi.dev/oauth/" + id + "/client.json");
        return new McpOAuthCimdIdentity(id, documentUri, issuerResponses);
    }

    public static string GetServerId(Uri serverUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl.AbsoluteUri));
        return Convert.ToBase64String(hash.AsSpan(0, 9)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string ReadAuthorizationServer(JsonElement document)
    {
        if (document.ValueKind == JsonValueKind.Object &&
            document.TryGetProperty("authorization_servers", out var servers) &&
            servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0 &&
            servers[0].ValueKind == JsonValueKind.String &&
            Uri.TryCreate(servers[0].GetString(), UriKind.Absolute, out var server))
            return server.AbsoluteUri;
        throw new InvalidOperationException("MCP protected-resource metadata has no authorization server.");
    }

    private static Uri? ReadResourceMetadataUrl(HttpResponseMessage challenge)
    {
        foreach (var value in challenge.Headers.WwwAuthenticate)
        {
            if (!value.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) || value.Parameter is null) continue;
            var match = Regex.Match(value.Parameter, "(?:^|,)\\s*resource_metadata=\"(?<uri>[^\"]+)\"",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (match.Success && Uri.TryCreate(match.Groups["uri"].Value, UriKind.Absolute, out var uri)) return uri;
        }
        return null;
    }

    private static Uri GetResourceMetadataUrl(Uri serverUrl)
    {
        var path = serverUrl.AbsolutePath.TrimEnd('/');
        return new Uri(serverUrl.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource" + path);
    }

    private static async Task<JsonDocument> ReadAuthorizationMetadataAsync(HttpClient http,
        IReadOnlyList<Uri> endpoints, CancellationToken cancellationToken)
    {
        foreach (var endpoint in endpoints)
        {
            using var response = await http.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false));
        }
        throw new InvalidOperationException("MCP authorization-server metadata could not be loaded for CIMD.");
    }

    private static IReadOnlyList<Uri> GetAuthorizationMetadataUrls(string issuer)
    {
        var uri = new Uri(issuer);
        var host = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath.Trim('/');
        var paths = path.Length == 0
            ? new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration" }
            : new[] { "/.well-known/oauth-authorization-server/" + path,
                "/.well-known/openid-configuration/" + path, "/" + path + "/.well-known/openid-configuration" };
        return paths.Select(item => new Uri(host + item)).ToArray();
    }
}

internal sealed record McpOAuthCimdIdentity(string Id, Uri ClientMetadataDocumentUri, bool IssuerResponses);

internal sealed class McpOAuthCimdTokenCache(ITokenCache inner, Uri documentUri) : ITokenCache
{
    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        var tokens = await inner.GetTokensAsync(cancellationToken).ConfigureAwait(false);
        if (tokens is null || string.Equals(tokens.ClientId, documentUri.AbsoluteUri, StringComparison.Ordinal))
            return tokens;

        // A cached DCR registration must not override the explicitly selected CIMD identity.
        tokens.ClientId = null;
        tokens.ClientSecret = null;
        tokens.TokenEndpointAuthMethod = null;
        tokens.AuthorizationServer = null;
        return tokens;
    }

    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken) =>
        inner.StoreTokensAsync(tokens, cancellationToken);
}
