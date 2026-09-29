using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Mcp;

/// <summary>Registers MCP through the same built-in extension selection as other runtime features.</summary>
public static class McpBuiltin
{
    public static BuiltinExtensionDefinition CreateDefinition(McpRuntimeManager manager) =>
        new("mcp", registration => registration.AddCommand("mcp", manager.HandleCommandAsync,
            "Show MCP server status or reconnect a server."));
}
