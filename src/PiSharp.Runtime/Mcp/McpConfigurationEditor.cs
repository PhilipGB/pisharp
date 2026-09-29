using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace PiSharp.Runtime.Mcp;

/// <summary>Updates one MCP config file without replacing unrelated settings or losing concurrent edits.</summary>
public static class McpConfigurationEditor
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static Task<bool> AddAsync(string path, string name, JsonElement server,
        CancellationToken cancellationToken = default) => EditAsync(path, name, server, cancellationToken);

    public static Task<bool> RemoveAsync(string path, string name,
        CancellationToken cancellationToken = default) => EditAsync(path, name, null, cancellationToken,
            remove: true, patch: null);

    public static Task<bool> UpdateAsync(string path, string name, bool? enabled, McpToolExposure? exposure,
        CancellationToken cancellationToken = default)
    {
        if (enabled is null && exposure is null)
            throw new ArgumentException("An MCP setting update is required.");
        return EditAsync(path, name, null, cancellationToken, remove: false, patch: server =>
        {
            if (enabled is { } value)
            {
                if (value) server.Remove("enabled");
                else server["enabled"] = false;
            }
            if (exposure is { } mode)
            {
                if (mode == McpToolExposure.Codemode) server.Remove("exposure");
                else server["exposure"] = mode switch
                {
                    McpToolExposure.CodemodeDeferred => "codemode-deferred",
                    _ => mode.ToString().ToLowerInvariant()
                };
            }
        });
    }

    private static async Task<bool> EditAsync(string path, string name, JsonElement? server,
        CancellationToken cancellationToken, bool remove = false, Action<JsonObject>? patch = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (server is { } value) McpConfiguration.Parse(name, value, fullPath, "global");
        var gate = s_gates.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await McpFileLock.AcquireAsync(fullPath + ".lock", TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(25), cancellationToken);
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
            if (remove)
            {
                if (!replaced) return false;
                servers.Remove(name);
            }
            else if (server is { } jsonServer) servers[name] = JsonNode.Parse(jsonServer.GetRawText());
            else
            {
                if (!servers.TryGetPropertyValue(name, out var selected) || selected is not JsonObject selectedServer)
                    return false;
                patch!(selectedServer);
            }
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
        finally { gate.Release(); }
    }
}
