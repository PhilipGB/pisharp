using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Tui;

internal static class InteractiveResourceCommands
{
    public static async Task ExecuteAsync(string command, string argument, string line, Func<ResourceCatalog> getResources,
        Func<ExtensionRegistration> getExtensions, Func<string, Task> run, TerminalEditor? editor, TerminalScreen? screen,
        Func<string, Task> applicationAction, Func<Task> reload,
        Func<IReadOnlyList<McpServerManagementInfo>> getMcpServers)
    {
        if (command.StartsWith("/skill:", StringComparison.Ordinal)) await run(line);
        else if (getResources().Prompts.Any(item => "/" + item.Name == command)) await run(line);
        else if (getExtensions().Commands.TryGetValue(command[1..], out var handler))
        {
            var arguments = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (command.Equals("/mcp", StringComparison.Ordinal) && arguments.Length == 0 &&
                editor is not null && screen is { IsActive: true })
            {
                var manager = new TerminalMcpManager(editor, getMcpServers,
                    action => RunMcpActionAsync(action, handler, editor, applicationAction));
                await manager.ShowAsync();
                return;
            }
            if (command.Equals("/mcp", StringComparison.Ordinal) && arguments is ["reload"])
            {
                await reload();
                Console.WriteLine("Reloaded MCP servers and interactive resources.");
                return;
            }
            if (command.Equals("/mcp", StringComparison.Ordinal) && arguments is ["help"])
            {
                Console.WriteLine("/mcp status | details <server> | reconnect <server> | enable <server> |");
                Console.WriteLine("  disable <server> |");
                Console.WriteLine("  exposure <server> <mode> | login <server> | logout <server> | reload");
                Console.WriteLine("Use /mcp status to list server states; /mcp details <server> shows sanitized configuration details.");
                Console.WriteLine("Use /mcp add ... or /mcp remove ... in the shell to change server definitions.");
                return;
            }
            if (command.Equals("/mcp", StringComparison.Ordinal) && arguments.Length > 0 &&
                arguments[0].Equals("login", StringComparison.Ordinal) && editor is not null && screen is { IsActive: true })
            {
                Console.WriteLine(await RunMcpActionAsync(argument, handler, editor, applicationAction));
            }
            else Console.WriteLine(await handler(argument, CancellationToken.None));
        }
        else Console.Error.WriteLine($"Unknown command: {command}");
    }

    private static async Task<string> RunMcpActionAsync(string argument,
        Func<string, CancellationToken, Task<string>> handler, TerminalEditor editor, Func<string, Task> applicationAction)
    {
        if (!argument.StartsWith("login ", StringComparison.Ordinal))
            return await handler(argument, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var signIn = handler(argument, cancellation.Token);
        await editor.MonitorMcpAuthorizationAsync(signIn, applicationAction, cancellation.Cancel);
        try { return await signIn; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return "MCP sign-in cancelled.";
        }
    }
}
