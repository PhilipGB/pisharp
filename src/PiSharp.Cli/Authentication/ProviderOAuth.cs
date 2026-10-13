using System.Collections.Concurrent;
using System.Text.Json;

namespace PiSharp.Cli;

public sealed record ProviderOAuthLoginMethod(string Id, string Label);

public sealed record ProviderOAuthNotice(string Kind, string Message, Uri? Url = null,
    string? UserCode = null, int? IntervalSeconds = null, int? ExpiresInSeconds = null);

public interface IProviderOAuthInteraction
{
    Task<string> SelectLoginMethodAsync(IReadOnlyList<ProviderOAuthLoginMethod> methods,
        CancellationToken cancellationToken);

    Task<string> PromptForCodeAsync(string message, string placeholder, CancellationToken cancellationToken) =>
        Task.FromException<string>(new NotSupportedException("Manual OAuth code input is not supported by this interaction."));

    void Notify(ProviderOAuthNotice notice);
}

public interface IProviderOAuthAdapter
{
    string ProviderId { get; }
    IReadOnlyList<ProviderOAuthLoginMethod> LoginMethods { get; }
    Task<StoredCredential> LoginAsync(string method, IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken);
    Task<StoredCredential> RefreshAsync(StoredCredential credential, CancellationToken cancellationToken);
    bool IsValidCredential(StoredCredential credential, out string reason);
}

public interface IProviderOAuthCallbackServer : IAsyncDisposable
{
    Task<string> WaitForCodeAsync(CancellationToken cancellationToken);
}

public interface IProviderOAuthCallbackServerFactory
{
    IProviderOAuthCallbackServer Start(Uri redirectUri, string expectedState);
}

internal sealed class ProviderOAuthCoordinator
{
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
    private readonly AuthStorage _storage;
    private readonly IReadOnlyDictionary<string, IProviderOAuthAdapter> _adapters;

    public ProviderOAuthCoordinator(AuthStorage storage, HttpClient http,
        IEnumerable<IProviderOAuthAdapter>? adapters = null)
    {
        _storage = storage;
        var configured = adapters?.ToArray() ?? [new OpenAiCodexOAuthAdapter(http)];
        _adapters = configured.ToDictionary(adapter => adapter.ProviderId, StringComparer.OrdinalIgnoreCase);
    }

    public bool Supports(string providerId) => _adapters.ContainsKey(providerId);

    public async Task LoginAsync(string providerId, IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(providerId, out var adapter))
            throw new InvalidOperationException($"Provider '{providerId}' has no configured OAuth adapter.");
        var method = await interaction.SelectLoginMethodAsync(adapter.LoginMethods, cancellationToken)
            .ConfigureAwait(false);
        var credential = await adapter.LoginAsync(method, interaction, cancellationToken).ConfigureAwait(false);
        if (!adapter.IsValidCredential(credential, out var reason))
            throw new InvalidDataException($"OAuth login for '{providerId}' returned an invalid credential: {reason}");
        await _storage.StoreOAuthAsync(providerId, credential.Access!, credential.Refresh,
            credential.Expires, credential.AccountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(string Access, bool Authenticated, string Source)> ResolveAsync(string providerId,
        StoredCredential credential, bool allowRefresh, CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(providerId, out var adapter))
            return ("not-configured", false, "OAuth unsupported for provider");
        if (!adapter.IsValidCredential(credential, out _))
            return ("not-configured", false, "stored OAuth credential invalid");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var refreshAfter = now + (long)RefreshWindow.TotalMilliseconds;
        if (credential.Expires > refreshAfter)
            return (credential.Access!, true, "stored OAuth");
        if (!allowRefresh)
            return (credential.Access!, true, "stored OAuth (refresh on request)");
        if (string.IsNullOrWhiteSpace(credential.Refresh))
            return ("not-configured", false, "stored OAuth credential cannot be refreshed");

        await using var lease = await OAuthRefreshLease.AcquireAsync(_storage.Path, providerId, cancellationToken)
            .ConfigureAwait(false);
        var latest = await _storage.ReadAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (latest is null || latest.Type != "oauth")
            return ("not-configured", false, "stored OAuth credential changed during refresh");
        if (!adapter.IsValidCredential(latest, out _))
            return ("not-configured", false, "stored OAuth credential invalid");
        now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        refreshAfter = now + (long)RefreshWindow.TotalMilliseconds;
        if (latest.Expires > refreshAfter)
            return (latest.Access!, true, "stored OAuth");
        if (string.IsNullOrWhiteSpace(latest.Refresh))
            return ("not-configured", false, "stored OAuth credential cannot be refreshed");

        StoredCredential refreshed;
        try
        {
            refreshed = await adapter.RefreshAsync(latest, cancellationToken).ConfigureAwait(false);
            if (!adapter.IsValidCredential(refreshed, out var reason))
                throw new InvalidDataException($"OAuth refresh returned an invalid credential: {reason}");
            await _storage.StoreOAuthAsync(providerId, refreshed.Access!, refreshed.Refresh,
                refreshed.Expires, refreshed.AccountId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            return ("not-configured", false, "OAuth refresh failed; run /login");
        }
        return (refreshed.Access!, true, "stored OAuth");
    }

    public async Task<(string Access, string AccountId)> ResolveCredentialAsync(string providerId,
        CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(providerId, out var adapter))
            throw new InvalidOperationException($"OAuth is unsupported for '{providerId}'.");
        var credential = await _storage.ReadAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (credential is null || credential.Type != "oauth")
            throw new InvalidOperationException($"Stored OAuth credentials for '{providerId}' are unavailable; run /login.");
        var resolved = await ResolveAsync(providerId, credential, allowRefresh: true, cancellationToken)
            .ConfigureAwait(false);
        if (!resolved.Authenticated)
            throw new InvalidOperationException($"Stored OAuth credentials for '{providerId}' are unavailable; run /login.");
        var current = await _storage.ReadAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Type != "oauth" || current.Access != resolved.Access ||
            !adapter.IsValidCredential(current, out _))
            throw new InvalidOperationException($"Stored OAuth credentials for '{providerId}' changed; run /login.");
        return (resolved.Access, current.AccountId!);
    }
}

internal sealed class OAuthRefreshLease : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _processGate;
    private readonly FileStream _lockFile;
    private bool _disposed;

    private OAuthRefreshLease(SemaphoreSlim processGate, FileStream lockFile)
    {
        _processGate = processGate;
        _lockFile = lockFile;
    }

    public static async Task<OAuthRefreshLease> AcquireAsync(string authPath, string provider,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(authPath);
        var key = fullPath + "|" + provider;
        var processGate = ProcessGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var lockPath = fullPath + "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(provider.ToUpperInvariant())))[..16] + ".refresh.lock";
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.Asynchronous);
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    return new OAuthRefreshLease(processGate, lockFile);
                }
                catch (IOException) when (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            processGate.Release();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _lockFile.Dispose();
        _processGate.Release();
        return ValueTask.CompletedTask;
    }
}
