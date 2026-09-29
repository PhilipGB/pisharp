namespace PiSharp.Runtime.Mcp;

/// <summary>Session-scoped status and recovery actions exposed by the built-in /mcp command.</summary>
public sealed class McpRuntimeManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (McpServerConfiguration Configuration, McpServerConnection? Connection)> _servers =
        new(StringComparer.Ordinal);

    internal void Register(McpServerConfiguration configuration, McpServerConnection? connection)
    {
        lock (_gate) _servers[configuration.Name] = (configuration, connection);
    }

    public async Task<string> HandleCommandAsync(string arguments, CancellationToken cancellationToken)
    {
        var parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts is ["status"])
            return FormatStatus();
        if (parts is ["reconnect", var name])
        {
            (McpServerConfiguration Configuration, McpServerConnection? Connection) server;
            lock (_gate)
            {
                if (!_servers.TryGetValue(name, out server))
                    return "Unknown MCP server: " + name;
            }
            if (!server.Configuration.Enabled || server.Connection is null)
                return "MCP server " + name + " is disabled or unavailable.";
            try
            {
                await server.Connection.ReconnectAsync(cancellationToken);
                return "Reconnected MCP server " + name + ".";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                return "Could not reconnect MCP server " + name + " (" + error.GetType().Name + ").";
            }
        }
        return "Usage: /mcp [status | reconnect <server>]";
    }

    private string FormatStatus()
    {
        (McpServerConfiguration Configuration, McpServerConnection? Connection)[] servers;
        lock (_gate) servers = _servers.Values.OrderBy(item => item.Configuration.Name, StringComparer.Ordinal).ToArray();
        if (servers.Length == 0) return "No MCP servers configured.";
        return string.Join(Environment.NewLine, servers.Select(item =>
        {
            var state = !item.Configuration.Enabled ? "disabled" : item.Connection?.State ?? "unavailable";
            var tools = item.Connection?.Tools.Count ?? 0;
            var line = item.Configuration.Name + ": " + state + " (" + item.Configuration.Exposure.ToString()
                .ToLowerInvariant().Replace("codemodedeferred", "codemode-deferred", StringComparison.Ordinal)
                + ", " + item.Configuration.Scope + ", " + tools + " tools)";
            return item.Connection?.Error is { } error ? line + " — " + error : line;
        }));
    }
}
