using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Authentication;

namespace PiSharp.Runtime.Mcp;

/// <summary>OAuth settings for an HTTP MCP server. The SDK owns PKCE, discovery and refresh.</summary>
public sealed record McpOAuthSettings(string? ClientId, string? ClientSecret, Uri? CallbackUrl,
    IReadOnlyList<string> Scopes)
{
    public string? ClientName { get; init; }

    public static McpOAuthSettings Parse(JsonElement value)
    {
        string? Read(string name)
        {
            if (!value.TryGetProperty(name, out var property)) return null;
            if (property.ValueKind != JsonValueKind.String)
                throw new ArgumentException("MCP oauth." + name + " must be a string.");
            return property.GetString();
        }

        var known = new[] { "clientId", "clientSecret", "callbackUrl", "scope", "clientName" };
        if (value.EnumerateObject().Any(property => !known.Contains(property.Name, StringComparer.Ordinal)))
            throw new ArgumentException("MCP OAuth has an unknown option.");
        var clientName = Read("clientName");
        if (clientName is not null && string.IsNullOrWhiteSpace(clientName))
            throw new ArgumentException("MCP oauth.clientName must be a non-empty string.");
        var callback = Read("callbackUrl");
        Uri? redirect = null;
        if (callback is not null)
        {
            if (!Uri.TryCreate(callback, UriKind.Absolute, out redirect) || redirect.Scheme != "http" ||
                redirect.Host is not ("127.0.0.1" or "localhost" or "::1") || redirect.IsDefaultPort ||
                !string.IsNullOrEmpty(redirect.Query) || !string.IsNullOrEmpty(redirect.Fragment))
                throw new ArgumentException("MCP OAuth callbackUrl must be a loopback HTTP URL with a port.");
        }
        return new McpOAuthSettings(Read("clientId"), Read("clientSecret"), redirect,
            (Read("scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { ClientName = clientName };
    }

    public ClientOAuthOptions CreateOptions(Uri serverUrl, ITokenCache cache, Uri redirectUri,
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

/// <summary>Process-safe, private OAuth token cache, keyed by the absolute MCP resource URL.</summary>
public sealed class McpTokenCache(string agentDirectory)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_refreshGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(agentDirectory, "mcp-auth.json");
    private readonly ConcurrentDictionary<string, McpRefreshFileLease> _pendingRefreshes = new(StringComparer.Ordinal);

    public ITokenCache ForServer(Uri url) => ForServerWithRefresh(url);

    internal ServerCache ForServerWithRefresh(Uri url) => new(this, Key(url));

    public async Task<bool> RemoveAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var key = Key(url);
        await using var refreshLease = await AcquireRefreshLockAsync(key, cancellationToken);
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
            await using var lease = await McpFileLock.AcquireAsync(_path + ".lock", TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(25), cancellationToken);
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
        finally { gate.Release(); }
    }

    internal async Task<McpRefreshFileLease> AcquireRefreshLockAsync(Uri url,
        CancellationToken cancellationToken) => await AcquireRefreshLockAsync(Key(url), cancellationToken);

    private async Task<McpRefreshFileLease> AcquireRefreshLockAsync(string key,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(_path);
        var gateKey = path + "\0" + key;
        var gate = s_refreshGates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            var lockPath = Path.Combine(Path.GetDirectoryName(path)!, "mcp-auth-refresh-" + suffix + ".lock");
            var fileLock = await McpFileLock.AcquireAsync(lockPath, TimeSpan.FromSeconds(25),
                TimeSpan.FromMilliseconds(50), cancellationToken);
            return new McpRefreshFileLease(fileLock, gate);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    internal async Task<TokenContainer?> ReadTokensAsync(Uri url, CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken)).GetValueOrDefault(Key(url));

    internal void TrackPendingRefresh(Uri url, McpRefreshFileLease lease)
    {
        if (!_pendingRefreshes.TryAdd(Key(url), lease))
            throw new InvalidOperationException("An MCP OAuth token grant is already being persisted.");
    }

    internal bool HasPendingRefresh(Uri url) => _pendingRefreshes.ContainsKey(Key(url));

    internal async Task WaitForPendingRefreshAsync(Uri url)
    {
        var key = Key(url);
        while (_pendingRefreshes.TryGetValue(key, out var lease))
            await lease.Settled;
    }

    private async ValueTask StoreAsync(string key, TokenContainer tokens, CancellationToken cancellationToken)
    {
        _pendingRefreshes.TryGetValue(key, out var pending);
        try
        {
            await EditAsync(values => { values[key] = tokens; return true; },
                pending is null ? cancellationToken : CancellationToken.None);
        }
        finally
        {
            if (pending is not null)
            {
                await pending.DisposeAsync();
                _pendingRefreshes.TryRemove(key, out _);
            }
        }
    }

    internal sealed class ServerCache(McpTokenCache owner, string key) : ITokenCache
    {
        private TokenContainer? _lastObserved;
        internal McpTokenCache Owner => owner;
        internal string Key => key;
        internal TokenContainer? LastObserved => Volatile.Read(ref _lastObserved);

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            var tokens = (await owner.ReadAsync(cancellationToken)).GetValueOrDefault(key);
            Volatile.Write(ref _lastObserved, tokens);
            return tokens;
        }

        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            await owner.StoreAsync(key, tokens, cancellationToken);
        }
    }
}

internal sealed class McpRefreshFileLease(McpFileLock fileLock, SemaphoreSlim gate) : IAsyncDisposable
{
    private int _disposed;
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Settled => _settled.Task;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { fileLock.Dispose(); }
        finally
        {
            gate.Release();
            _settled.TrySetResult();
        }
    }
}

public sealed class McpSignInRequiredException : Exception
{
    public McpSignInRequiredException() : base("MCP server requires explicit sign-in.") { }
}
