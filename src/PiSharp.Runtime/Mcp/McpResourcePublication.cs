using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Mcp;

/// <summary>Publishes shared resource tools as server capability/exposure changes arrive.</summary>
internal sealed class McpResourcePublication(ExtensionRegistration registration)
{
    private const string Owner = "mcp:resources";
    private static readonly HashSet<string> s_names = new(StringComparer.Ordinal)
        { "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource" };
    private readonly object _catalogGate = new();
    private readonly object _publicationGate = new();
    private readonly Dictionary<string,
        (string Name, McpServerConnection Connection, TimeSpan Timeout, ToolExposure Exposure)> _servers = new(StringComparer.Ordinal);

    public bool HasNameConflict
    {
        get
        {
            var owned = registration.GetOwnedToolNames(Owner).ToHashSet(StringComparer.Ordinal);
            bool hasServers;
            lock (_catalogGate) hasServers = _servers.Count > 0;
            return hasServers && registration.ToolDefinitions.Any(tool =>
                s_names.Contains(tool.Function.Name) && !owned.Contains(tool.Function.Name));
        }
    }

    public void Update(McpServerConfiguration server, McpServerConnection? connection)
    {
        lock (_catalogGate)
        {
            if (connection is not null && server.Enabled && connection.HasResources &&
                server.Exposure != McpToolExposure.Hidden)
                _servers[server.Name] = (server.Name, connection, server.Timeout,
                    McpToolPublication.MapExposure(server.Exposure));
            else _servers.Remove(server.Name);
        }
        Refresh();
    }

    private void Refresh()
    {
        lock (_publicationGate)
        {
            var owned = registration.GetOwnedToolNames(Owner).ToHashSet(StringComparer.Ordinal);
            if (registration.ToolDefinitions.Any(tool => s_names.Contains(tool.Function.Name) &&
                    !owned.Contains(tool.Function.Name))) return;
            (string Name, McpServerConnection Connection, TimeSpan Timeout, ToolExposure Exposure)[] current;
            lock (_catalogGate) current = _servers.Values.ToArray();
            var source = new ResourceSourceInfo("builtin:mcp", "builtin", "builtin", "top-level", null);
            if (current.Length == 0)
            {
                registration.ReplaceOwnedTools(Owner, [], source);
                return;
            }
            new McpResourceTools(current).Configure(registration);
        }
    }
}
