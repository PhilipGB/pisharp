using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Authentication;

namespace PiSharp.Runtime.Mcp;

/// <summary>Process-safe, private OAuth token cache, keyed by normalized server name and resource URL.</summary>
public sealed class McpTokenCache(string agentDirectory)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_refreshGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path = Path.Combine(agentDirectory, "mcp-auth.json");
    private readonly ConcurrentDictionary<string, McpRefreshFileLease> _pendingRefreshes = new(StringComparer.Ordinal);

    public ITokenCache ForServer(string name, Uri url) => ForServerWithRefresh(name, url);

    internal ServerCache ForServerWithRefresh(string name, Uri url) => new(this, Key(name, url), url.AbsoluteUri);

    public async Task<bool> RemoveAsync(string name, Uri url, CancellationToken cancellationToken = default)
    {
        var key = Key(name, url);
        await using var refreshLease = await AcquireRefreshLockAsync(key, cancellationToken);
        return await EditAsync(values => values.Remove(key) || values.Remove(url.AbsoluteUri), cancellationToken);
    }

    private static string Key(string name, Uri url) => McpToolIdentifiers.Namespace(name) + "|" + url.AbsoluteUri;

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

    internal async Task<McpRefreshFileLease> AcquireRefreshLockAsync(string key,
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

    internal async Task<TokenContainer?> ReadTokensAsync(string key, string legacyKey,
        CancellationToken cancellationToken, bool migrate = false)
    {
        TokenContainer? tokens = null;
        await EditAsync(values =>
        {
            if (values.TryGetValue(key, out tokens) || !values.TryGetValue(legacyKey, out tokens)) return false;
            if (!migrate) return false;
            values[key] = tokens;
            values.Remove(legacyKey);
            return true;
        }, cancellationToken);
        return tokens;
    }

    internal void TrackPendingRefresh(string key, McpRefreshFileLease lease)
    {
        if (!_pendingRefreshes.TryAdd(key, lease))
            throw new InvalidOperationException("An MCP OAuth token grant is already being persisted.");
    }

    internal bool HasPendingRefresh(string key) => _pendingRefreshes.ContainsKey(key);

    internal async Task WaitForPendingRefreshAsync(string key)
    {
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

    internal sealed class ServerCache(McpTokenCache owner, string key, string legacyKey) : ITokenCache
    {
        private TokenContainer? _lastObserved;
        internal McpTokenCache Owner => owner;
        internal string Key => key;
        internal string LegacyKey => legacyKey;
        internal TokenContainer? LastObserved => Volatile.Read(ref _lastObserved);

        public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            var tokens = await owner.ReadTokensAsync(key, legacyKey, cancellationToken, migrate: true);
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

