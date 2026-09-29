using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace PiSharp.Runtime.Mcp;

/// <summary>Updates one MCP config file without replacing unrelated settings or losing concurrent edits.</summary>
public static class McpConfigurationEditor
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static Task<bool> AddAsync(string path, string name, JsonElement server,
        CancellationToken cancellationToken = default) => EditAsync(path, name, server, cancellationToken);

    public static Task<bool> RemoveAsync(string path, string name,
        CancellationToken cancellationToken = default) => EditAsync(path, name, null, cancellationToken);

    private static async Task<bool> EditAsync(string path, string name, JsonElement? server,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (server is { } value) McpConfiguration.Parse(name, value, fullPath, "global");
        var gate = s_gates.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (OperatingSystem.IsLinux()) Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var lockOptions = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite
            };
            if (OperatingSystem.IsLinux()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var lease = new FileStream(fullPath + ".lock", lockOptions);
            if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("MCP config locking is not supported on macOS.");
            var elapsed = Stopwatch.StartNew();
            while (true)
                try { lease.Lock(0, 1); break; }
                catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(25, cancellationToken);
                }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                JsonObject root;
                if (!File.Exists(fullPath)) root = new JsonObject();
                else
                {
                    if (new FileInfo(fullPath).Length > 1024 * 1024)
                        throw new InvalidDataException("MCP config exceeds 1 MiB.");
                    root = JsonNode.Parse(await File.ReadAllTextAsync(fullPath, cancellationToken)) as JsonObject ??
                        throw new InvalidDataException("MCP config must be an object.");
                }
                if (root.ContainsKey("mcpServers") && root["mcpServers"] is not JsonObject)
                    throw new InvalidDataException("MCP mcpServers must be an object.");
                var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
                var replaced = servers.ContainsKey(name);
                if (server is null)
                {
                    if (!replaced) return false;
                    servers.Remove(name);
                }
                else servers[name] = JsonNode.Parse(server.Value.GetRawText());
                if (root["mcpServers"] is null) root["mcpServers"] = servers;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true });
                var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    var options = new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Options = FileOptions.Asynchronous
                    };
                    if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using (var output = new FileStream(temporary, options))
                    {
                        await output.WriteAsync(bytes, cancellationToken);
                        output.Flush(flushToDisk: true);
                    }
                    File.Move(temporary, fullPath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return replaced;
            }
            finally { lease.Unlock(0, 1); }
        }
        finally { gate.Release(); }
    }
}
