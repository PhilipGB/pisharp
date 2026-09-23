using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace PiSharp.Runtime;

/// <summary>
/// Atomic, private MAF session snapshots for resuming a linear conversation. This is not the
/// canonical session tree required for Pi parity; see docs/architecture.md before extending it.
/// </summary>
public sealed class SessionSnapshots(string workingDirectory, string model, string? endpoint, string? directory = null)
{
    private readonly string _cwd = Path.GetFullPath(workingDirectory);
    private readonly string _model = model;
    private readonly string? _endpoint = endpoint;
    public string DirectoryPath { get; } = directory ?? DefaultDirectory(workingDirectory);

    private static string DefaultDirectory(string cwd)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(cwd)))).ToLowerInvariant()[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pisharp", "sessions", hash);
    }

    public string NewPath() => Path.Combine(DirectoryPath, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}_{Guid.NewGuid():N}.json");

    public string? MostRecentPath() => System.IO.Directory.Exists(DirectoryPath)
        ? System.IO.Directory.EnumerateFiles(DirectoryPath, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;

    public async Task SaveAsync(PiAgent agent, AgentSession session, string path, CancellationToken cancellationToken = default)
    {
        var state = await agent.SerializeSessionAsync(session, cancellationToken);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Snapshot(1, _cwd, _model, _endpoint, state));
        var destination = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(destination)!;
        System.IO.Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, ".pisharp-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, payload, cancellationToken);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<AgentSession> LoadAsync(PiAgent agent, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var snapshot = await JsonSerializer.DeserializeAsync<Snapshot>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Empty session snapshot.");
        if (snapshot.Version != 1 || snapshot.Cwd != _cwd || snapshot.Model != _model || snapshot.Endpoint != _endpoint)
            throw new InvalidDataException("Session version, project directory, model or endpoint differs. Refusing to restore an incompatible provider session.");
        return await agent.DeserializeSessionAsync(snapshot.State, cancellationToken);
    }

    public sealed record Snapshot(int Version, string Cwd, string Model, string? Endpoint, JsonElement State);
}
