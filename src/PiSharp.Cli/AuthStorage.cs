using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSharp.Cli;

public sealed record StoredCredential(string Type, string? Key = null, string? Access = null,
    string? Refresh = null, long? Expires = null)
{
    public string? Secret => Type == "oauth" ? Access : Key;
}

/// <summary>Private provider credential storage. Callers only display type/source metadata, never this payload.</summary>
public sealed class AuthStorage(string path)
{
    private static readonly Regex ProviderId = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private readonly string _path = System.IO.Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path => _path;

    public async Task<StoredCredential?> ReadAsync(string provider, CancellationToken cancellationToken = default)
    {
        ValidateProvider(provider);
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAllCoreAsync(cancellationToken)).GetValueOrDefault(provider); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, string>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAllCoreAsync(cancellationToken)).ToDictionary(item => item.Key, item => item.Value.Type, StringComparer.OrdinalIgnoreCase); }
        finally { _gate.Release(); }
    }

    public async Task StoreApiKeyAsync(string provider, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("API key cannot be empty.", nameof(key));
        await StoreAsync(provider, new StoredCredential("api_key", Key: key), cancellationToken);
    }

    /// <summary>Stores a pre-issued OAuth bearer token. Browser authorization and refresh are provider-adapter responsibilities.</summary>
    public async Task StoreOAuthAsync(string provider, string accessToken, string? refreshToken = null,
        long? expires = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("OAuth access token cannot be empty.", nameof(accessToken));
        await StoreAsync(provider, new StoredCredential("oauth", Access: accessToken, Refresh: refreshToken, Expires: expires), cancellationToken);
    }

    public async Task DeleteAsync(string provider, CancellationToken cancellationToken = default)
    {
        ValidateProvider(provider);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllCoreAsync(cancellationToken);
            if (all.Remove(provider)) await WriteAllCoreAsync(all, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task StoreAsync(string provider, StoredCredential credential, CancellationToken cancellationToken)
    {
        ValidateProvider(provider);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllCoreAsync(cancellationToken);
            all[provider] = credential;
            await WriteAllCoreAsync(all, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, StoredCredential>> ReadAllCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        var info = new FileInfo(_path);
        if (info.LinkTarget is not null) throw new InvalidDataException("Refusing a symbolic-link auth.json.");
        if (info.Length > 64 * 1024) throw new InvalidDataException("auth.json exceeds 64KB.");
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(_path) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidDataException("auth.json is not private; remove group/other permissions before loading.");
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var data = await JsonSerializer.DeserializeAsync<Dictionary<string, StoredCredential>>(stream,
                cancellationToken: cancellationToken) ?? [];
            foreach (var (provider, credential) in data)
            {
                ValidateProvider(provider);
                if (credential.Type == "api_key" && !string.IsNullOrWhiteSpace(credential.Key)) continue;
                if (credential.Type == "oauth" && !string.IsNullOrWhiteSpace(credential.Access)) continue;
                throw new InvalidDataException($"Invalid credential entry for provider '{provider}'.");
            }
            return new(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid auth.json; expected provider credential objects.", error); }
    }

    private async Task WriteAllCoreAsync(Dictionary<string, StoredCredential> data, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream, data, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, _path, true);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateProvider(string provider)
    {
        if (!ProviderId.IsMatch(provider)) throw new ArgumentException("Invalid provider ID.", nameof(provider));
    }
}

public static class SecretRedactor
{
    public static string Redact(string message, params string?[] secrets)
    {
        foreach (var secret in secrets.Where(value => !string.IsNullOrEmpty(value)).OrderByDescending(value => value!.Length))
            message = message.Replace(secret!, "[REDACTED]", StringComparison.Ordinal);
        return message;
    }
}
