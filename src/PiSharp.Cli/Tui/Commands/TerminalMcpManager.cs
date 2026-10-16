using PiSharp.Runtime.Mcp;

namespace PiSharp.Cli.Tui;

/// <summary>Interactive server and action menus for the built-in MCP manager.</summary>
internal sealed class TerminalMcpManager(TerminalEditor editor,
    Func<IReadOnlyList<McpServerManagementInfo>> getServers,
    Func<string, Task<string>> runAction)
{
    public async Task ShowAsync()
    {
        string? selectedName = null;
        while (true)
        {
            var servers = OrderForAttention(getServers());
            var selection = editor.ShowSelectionList("MCP servers", servers.Select(server =>
                new TerminalSelectionOption<McpServerManagementInfo>(server.Name, server, server.Name,
                    ServerSummary(server), SearchText: server.Name + " " + ServerSummary(server))).ToArray(),
                selectedName, emptyMessage: "No MCP servers configured. Add one with pisharp mcp add.",
                actionHint: "Enter manage · Esc close");
            if (selection is null) return;
            selectedName = selection.Option.Key;

            while (true)
            {
                var server = getServers().FirstOrDefault(item => item.Name == selectedName);
                if (server is null) break;
                var actions = ActionsFor(server);
                var details = new List<string>
                {
                    "Transport: " + server.Transport + " (" + server.Endpoint + ")",
                    "Scope: " + server.Scope,
                    "Enabled: " + (server.Enabled ? "yes" : "no"),
                    "State: " + server.State,
                    "Exposure: " + ExposureName(server.Exposure),
                    "Auth: " + (server.AuthMode.Contains("header", StringComparison.Ordinal)
                        ? server.AuthMode + " (value hidden)" : server.AuthMode)
                };
                if (server.Error is { Length: > 0 } error) details.Add("Error: " + error);
                var chosen = editor.ShowInlineSelectionList("MCP server " + server.Name,
                    actions.Select(action => new TerminalSelectionOption<string>(action.Key, action.Key,
                        action.Label, action.Description)).ToArray(), details,
                    "↑↓ move · type to filter · Enter select · Esc back", selectedKey: null,
                    bottomMargin: 2);
                if (chosen is null) break;
                if (chosen.Option.Value == "tools")
                {
                    var toolOptions = server.Tools.Select(tool => new TerminalSelectionOption<string>(tool, tool,
                        tool, SearchText: tool)).ToArray();
                    editor.ShowSelectionList("Tools of " + server.Name, toolOptions,
                        emptyMessage: "The server offers no tools.", actionHint: "Enter or Esc back");
                    continue;
                }
                if (chosen.Option.Value == "exposure")
                {
                    var exposures = new[] { "codemode", "deferred", "direct", "hidden" };
                    var exposureChoice = editor.ShowSelectionList("Exposure of " + server.Name,
                        exposures.Select(exposure => new TerminalSelectionOption<string>(exposure, exposure,
                            exposure == ExposureName(server.Exposure) ? "✓ " + exposure : "  " + exposure,
                            exposure == ExposureName(server.Exposure) ? "Current exposure" : "Change tool exposure"))
                            .ToArray(), ExposureName(server.Exposure), actionHint: "Enter save · Esc back");
                    if (exposureChoice is not null)
                        Console.WriteLine(await runAction("exposure " + server.Name + " " + exposureChoice.Option.Value));
                    continue;
                }

                var action = chosen.Option.Value switch
                {
                    "enable" => "enable " + server.Name,
                    "disable" => "disable " + server.Name,
                    "login" => "login " + server.Name,
                    "logout" => "logout " + server.Name,
                    "reconnect" => "reconnect " + server.Name,
                    _ => null
                };
                if (action is not null) Console.WriteLine(await runAction(action));
            }
        }
    }

    private static IReadOnlyList<McpServerManagementInfo> OrderForAttention(
        IReadOnlyList<McpServerManagementInfo> servers) => servers
        .OrderBy(server => server.State switch
        {
            "needs-auth" or "failed" or "disconnected" => 0,
            "connecting" or "unavailable" => 1,
            _ => 2
        })
        .ThenBy(server => server.Name, StringComparer.Ordinal)
        .ToArray();

    private static string ServerSummary(McpServerManagementInfo server) =>
        server.State + " · " + ExposureName(server.Exposure) + " · " + server.Scope;

    private static string ExposureName(McpToolExposure exposure) => exposure switch
    {
        McpToolExposure.CodemodeDeferred => "codemode-deferred",
        _ => exposure.ToString().ToLowerInvariant()
    };

    private static IReadOnlyList<(string Key, string Label, string Description)> ActionsFor(
        McpServerManagementInfo server)
    {
        if (!server.Enabled)
            return [("enable", "Enable", "Enable this MCP server")];

        var actions = new List<(string Key, string Label, string Description)>();
        if (server.State == "connected")
            actions.Add(("tools", "Tools", server.Tools.Count + " offered"));
        if (server.State is "needs-auth" or "failed" or "disconnected" or "connected")
            actions.Add(("reconnect", "Reconnect", "Connect to this server again"));
        if (server.State == "needs-auth")
            actions.Add(("login", "Sign in", "Open the OAuth sign-in flow"));
        if (server.UsesOAuth && server.State == "connected")
            actions.Add(("logout", "Sign out", "Remove stored OAuth credentials"));
        actions.Add(("exposure", "Exposure", ExposureName(server.Exposure)));
        actions.Add(("disable", "Disable", "Disable this MCP server"));
        return actions;
    }
}
