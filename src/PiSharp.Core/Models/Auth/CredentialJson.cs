using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Core.Models.Auth;

/// <summary>
/// auth.json credential (de)serialization. The persisted shape matches pinned Pi
/// exactly: a provider-id map to type-tagged credential objects, written with 2-space
/// indentation and file mode 0600 under a 0700 agent directory.
/// </summary>
public static class CredentialJson
{
    /// <summary>Serializes a credential map to auth.json text (2-space indent, Pi format).</summary>
    public static string Serialize(IReadOnlyDictionary<string, Credential> credentials)
    {
        var root = new JsonObject();
        foreach (var (providerId, credential) in credentials)
        {
            root[providerId] = SerializeCredential(credential);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return root.ToJsonString(options);
    }

    private static JsonObject SerializeCredential(Credential credential)
    {
        var obj = new JsonObject();
        if (credential is ApiKeyCredential api)
        {
            obj["type"] = "api_key";
            if (api.Key is not null)
            {
                obj["key"] = api.Key;
            }

            return obj;
        }

        if (credential is OAuthCredential oauth)
        {
            obj["type"] = "oauth";
            obj["access"] = oauth.Access;
            obj["refresh"] = oauth.Refresh;
            obj["expires"] = oauth.Expires;
            AddIfPresent(obj, "clientId", oauth.ClientId);
            AddIfPresent(obj, "clientSecret", oauth.ClientSecret);
            AddIfPresent(obj, "apiKey", oauth.ApiKey);
            AddIfPresent(obj, "baseUrl", oauth.BaseUrl);
            AddIfPresent(obj, "proxyEndpoint", oauth.ProxyEndpoint);
            AddIfPresent(obj, "email", oauth.Email);
            AddIfPresent(obj, "scope", oauth.Scope);
            if (oauth.Extra is not null)
            {
                foreach (var (key, value) in oauth.Extra)
                {
                    obj[key] = value is null ? JsonValue.Create((string?)null) : JsonSerializer.SerializeToNode(value);
                }
            }

            return obj;
        }

        throw new NotSupportedException($"Unknown credential type: {credential.GetType()}");
    }

    private static void AddIfPresent(JsonObject obj, string key, string? value)
    {
        if (value is not null)
        {
            obj[key] = value;
        }
    }

    /// <summary>
    /// Parses auth.json content into a credential map, validating the shape exactly as
    /// pinned Pi does (auth-storage load): a non-object root and malformed entries are
    /// deterministic failures, not silent recovery.
    /// </summary>
    public static Dictionary<string, Credential> Deserialize(string content, string displayName = "auth.json")
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(JsonText.StripBom(content)).RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Failed to read {displayName}: {exception.Message}", exception);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Invalid {displayName}: expected an object");
        }

        var result = new Dictionary<string, Credential>();
        foreach (var entry in root.EnumerateObject())
        {
            result[entry.Name] = ParseCredential(entry.Name, entry.Value, displayName);
        }

        return result;
    }

    private static Credential ParseCredential(string providerId, JsonElement element, string displayName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Invalid {displayName} credential for provider \"{providerId}\"");
        }

        if (type.GetString() == "api_key")
        {
            var key = element.TryGetProperty("key", out var keyElement) && keyElement.ValueKind == JsonValueKind.String
                ? keyElement.GetString()
                : null;
            return new ApiKeyCredential(key);
        }

        if (type.GetString() == "oauth")
        {
            var access = RequireString(element, "access", providerId, displayName);
            var refresh = RequireString(element, "refresh", providerId, displayName);
            if (!element.TryGetProperty("expires", out var expiresElement) ||
                expiresElement.ValueKind != JsonValueKind.Number)
            {
                throw new InvalidOperationException($"Invalid {displayName} credential for provider \"{providerId}\"");
            }

            var extra = new Dictionary<string, object?>();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is not ("type" or "access" or "refresh" or "expires"
                    or "clientId" or "clientSecret" or "apiKey" or "baseUrl" or "proxyEndpoint" or "email" or "scope"))
                {
                    extra[property.Name] = ModelConfigParser.JsonValueToCSharp(property.Value);
                }
            }

            return new OAuthCredential
            {
                Access = access,
                Refresh = refresh,
                Expires = expiresElement.GetInt64(),
                ClientId = OptionalString(element, "clientId"),
                ClientSecret = OptionalString(element, "clientSecret"),
                ApiKey = OptionalString(element, "apiKey"),
                BaseUrl = OptionalString(element, "baseUrl"),
                ProxyEndpoint = OptionalString(element, "proxyEndpoint"),
                Email = OptionalString(element, "email"),
                Scope = OptionalString(element, "scope"),
                Extra = extra.Count > 0 ? extra : null,
            };
        }

        throw new InvalidOperationException($"Invalid {displayName} credential for provider \"{providerId}\"");
    }

    private static string RequireString(JsonElement element, string property, string providerId, string displayName)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidOperationException($"Invalid {displayName} credential for provider \"{providerId}\"");
    }

    private static string? OptionalString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
