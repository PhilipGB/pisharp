using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Authentication;

namespace PiSharp.Runtime.Mcp;

/// <summary>OAuth settings for an HTTP MCP server. The SDK owns PKCE, discovery and refresh.</summary>
public sealed record McpOAuthSettings(string? ClientId, string? ClientSecret, Uri? CallbackUrl,
    IReadOnlyList<string> Scopes)
{
    public static McpOAuthSettings Parse(JsonElement value)
    {
        string? Read(string name)
        {
            if (!value.TryGetProperty(name, out var property)) return null;
            if (property.ValueKind != JsonValueKind.String)
                throw new ArgumentException("MCP oauth." + name + " must be a string.");
            return property.GetString();
        }

        var known = new[] { "clientId", "clientSecret", "callbackUrl", "scope" };
        if (value.EnumerateObject().Any(property => !known.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException("MCP OAuth has an unknown option.");
        var callback = Read("callbackUrl");
        Uri? redirect = null;
        if (callback is not null)
        {
            if (!Uri.TryCreate(callback, UriKind.Absolute, out redirect) || redirect.Scheme != "http" ||
                redirect.Host is not ("127.0.0.1" or "localhost" or "::1") || redirect.IsDefaultPort ||
                !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment))
                throw new ArgumentException("MCP OAuth callbackUrl must be a loopback HTTP URL with a port.");
        }
        return new(Read("clientId"), Read("clientSecret"), redirect,
            (Read("scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public ClientOAuthOptions CreateOptions(Uri serverUrl, McpTokenCache cache, Uri redirectUri,
        Func<AuthorizationCallbackContext, CancellationToken, Task<AuthorizationResult?>> callback)
    {
        return new ClientOAuthOptions
        {
            RedirectUri = redirectUri,
            ClientId = ClientId,
            ClientSecret = ClientSecret is null ? null : Regex.Replace(ClientSecret,
                @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", match =>
                    Environment.GetEnvironmentVariable(match.Groups[1].Value) ??
                    throw new InvalidOperationException("Missing MCP OAuth secret environment variable.")),
            Scopes = Scopes,
            TokenCache = cache.ForServer(serverUrl),
            AuthorizationCallbackHandler = callback
        };
    }
}

/// <summary>Process-safe, private OAuth token cache, keyed by the absolute MCP resource URL.</summary>
public sealed class McpTokenCache(string agentDirectory)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(agentDirectory, "mcp-auth.json");

    public ITokenCache ForServer(Uri url) => new ServerCache(this, Key(url));

    public async Task<bool> RemoveAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var key = Key(url);
        return await EditAsync(values => values.Remove(key), cancellationToken);
    }

    private static string Key(Uri url) => url.AbsoluteUri;

    private async Task<Dictionary<string, TokenContainer>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("MCP credential file must not be a symbolic link.");
        if (new FileInfo(_path).Length > 1024 * 1024)
            throw new InvalidDataException("MCP credential file exceeds 1 MiB.");
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, TokenContainer>>(stream, s_json,
            cancellationToken) ?? throw new InvalidDataException("MCP credential file must be an object.");
    }

    private async Task<bool> EditAsync(Func<Dictionary<string, TokenContainer>, bool> edit,
        CancellationToken cancellationToken)
    {
        var gate = s_gates.GetOrAdd(Path.GetFullPath(_path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!, UnixFileMode.UserRead |
                    UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var lease = new FileStream(_path + ".lock", options);
            if (OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException("MCP credential locking is not supported on macOS.");
            var elapsed = Stopwatch.StartNew();
            while (true)
                try { lease.Lock(0, 1); break; }
                catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(10))
                { await Task.Delay(25, cancellationToken); }
            try
            {
                var values = await ReadAsync(cancellationToken);
                if (!edit(values)) return false;
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var create = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Options = FileOptions.Asynchronous
                    };
                    if (!OperatingSystem.IsWindows()) create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using (var stream = new FileStream(temporary, create))
                    {
                        await JsonSerializer.SerializeAsync(stream, values, s_json, cancellationToken);
                        stream.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, _path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return true;
            }
            finally { lease.Unlock(0, 1); }
        }
        finally { gate.Release(); }
    }

    private sealed class ServerCache(McpTokenCache owner, string key) : ITokenCache
    {
        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
            (await owner.ReadAsync(cancellationToken)).GetValueOrDefault(key);

        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            await owner.EditAsync(values => { values[key] = tokens; return true; }, cancellationToken);
        }
    }
}

public sealed class McpSignInRequiredException : Exception
{
    public McpSignInRequiredException() : base("MCP server requires explicit sign-in.") { }
}
