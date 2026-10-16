using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Mcp;

/// <summary>Connects configured MCP servers and registers their tools through the extension policy surface.</summary>
public static class McpRuntime
{
    private static readonly TimeSpan s_startupWait = TimeSpan.FromSeconds(10);

    public static Task<IReadOnlyList<string>> RegisterAsync(McpConfiguration configuration,
        ExtensionCatalog catalog, string workingDirectory, CancellationToken cancellationToken = default,
        McpRuntimeManager? manager = null,
        Func<string, CancellationToken, Task<string?>>? providerTokenResolver = null) =>
        RegisterAsync(configuration, catalog, workingDirectory, cancellationToken, manager, s_startupWait,
            providerTokenResolver: providerTokenResolver);

    internal static Task<IReadOnlyList<string>> RegisterForStatusAsync(McpConfiguration configuration,
        ExtensionCatalog catalog, string workingDirectory, CancellationToken cancellationToken = default,
        McpRuntimeManager? manager = null) =>
        RegisterAsync(configuration, catalog, workingDirectory, cancellationToken, manager,
            startupWait: s_startupWait, waitForAllServers: true);

    internal static async Task<IReadOnlyList<string>> RegisterAsync(McpConfiguration configuration,
        ExtensionCatalog catalog, string workingDirectory, CancellationToken cancellationToken,
        McpRuntimeManager? manager, TimeSpan startupWait, bool waitForAllServers = false,
        Func<string, CancellationToken, Task<string?>>? providerTokenResolver = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);
        if (startupWait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(startupWait));
        var errors = new List<string>(configuration.Errors);
        var registration = catalog.Registration;
        var startup = new McpStartupCoordinator(cancellationToken);
        catalog.OwnConnection(startup);
        var resources = new McpResourcePublication(registration);

        var configuredNames = configuration.Servers.Select(server => McpToolIdentifiers.Namespace(server.Name))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var extensionServer in registration.McpServers)
        {
            if (configuredNames.Contains(McpToolIdentifiers.Namespace(extensionServer.Configuration.Name)))
                errors.Add($"MCP server \"{extensionServer.Configuration.Name}\" registered by " +
                    extensionServer.ExtensionPath + " is overridden by configured MCP settings.");
        }
        var effectiveServers = SelectEffectiveServers(configuration.Servers, registration.McpServers);

        foreach (var server in effectiveServers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentServer = server;
            var publishGate = new object();
            McpServerConnection? connection = null;

            void Publish(McpServerConfiguration updated, McpServerConnection? published)
            {
                lock (publishGate)
                {
                    currentServer = updated;
                    if (published is null || !updated.Enabled)
                        registration.ReplaceOwnedTools("mcp:" + updated.Name, [],
                            new ResourceSourceInfo("builtin:mcp", "builtin", "builtin", "top-level", null));
                    else
                        McpToolPublication.Register(updated, published, registration, published.Tools, published.Instructions);

                    resources.Update(updated, published);
                }
            }

            McpServerConnection CreateConnection(McpServerConfiguration updated)
            {
                lock (publishGate) currentServer = updated;
                McpServerConnection? created = null;
                created = new McpServerConnection(updated.Name, updated.Url is not null,
                    token =>
                    {
                        McpServerConfiguration openingConfiguration;
                        lock (publishGate) openingConfiguration = currentServer;
                        return McpClientConnection.OpenAsync(openingConfiguration, configuration.AgentDirectory, workingDirectory,
                            authorizationUri => created?.SetPendingAuthorizationScope(
                                ReadAuthorizationScope(authorizationUri)),
                            providerTokenResolver,
                            (client, tools) =>
                            {
                                lock (publishGate)
                                {
                                    if (created?.IsCurrent(client) != true) return Task.CompletedTask;
                                    var activeConfiguration = currentServer;
                                    if (activeConfiguration.Enabled)
                                        McpToolPublication.Register(activeConfiguration, created, registration, tools,
                                            client.ServerInstructions);
                                    else
                                        registration.ReplaceOwnedTools("mcp:" + activeConfiguration.Name, [],
                                            new ResourceSourceInfo("builtin:mcp", "builtin", "builtin", "top-level", null));
                                    resources.Update(activeConfiguration, created);
                                }
                                return Task.CompletedTask;
                            }, token);
                    });
                catalog.OwnConnection(created);
                return created;
            }

            manager?.Register(server, null, CreateConnection, Publish);
            if (!server.Enabled) continue;

            connection = CreateConnection(server);
            manager?.Register(server, connection, CreateConnection, Publish);
            var task = ConnectServerAsync(server, connection, Publish, startup.CancellationToken);
            startup.Add(server, task);
        }

        if (effectiveServers.Any(server => server.Enabled))
        {
            registration.AddToolCallHook(async (call, token) =>
            {
                await startup.WaitForToolCallAsync(call, token).ConfigureAwait(false);
                return PiSharpToolCallDecision.Allow;
            });
            var result = await startup.WaitForServersAsync(startupWait, cancellationToken,
                directOnly: !waitForAllServers).ConfigureAwait(false);
            errors.AddRange(result.Errors);
            if (result.TimedOut)
                errors.AddRange(result.PendingServers.Select(name =>
                    $"MCP server {name} is still connecting; its tools become available once connected."));
        }

        if (resources.HasNameConflict)
            errors.Add("MCP resource tools could not register because a tool name is already in use.");
        return errors;
    }

    /// <summary>Renders the enabled indirect servers and current summaries for model context.</summary>
    public static string? RenderServerContext(IEnumerable<McpServerConfiguration> servers,
        McpRuntimeManager? manager = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var listed = servers.Where(server => server.Enabled && HasIndirectTools(server))
            .OrderBy(server => server.Name, StringComparer.Ordinal).ToArray();
        if (listed.Length == 0) return null;
        const int maximumSectionLength = 4096;
        const int maximumSummaryLength = 250;
        const string introduction = "MCP servers whose tools are not declared to you. Call the tools of `codemode` servers from codemode scripts: find them with `searchTools(query, { namespace })` and read a server's instructions and tool names with `describeNamespace(name)`. Load the tools of `tool_search` servers with `tool_search`.";
        var heads = listed.Select(server =>
        {
            var exposures = ConfiguredExposures(server);
            var route = exposures.Contains(McpToolExposure.Codemode) ||
                exposures.Contains(McpToolExposure.CodemodeDeferred) ? "codemode" : "tool_search";
            return "- " + McpToolIdentifiers.Namespace(server.Name) + " (" + route + ")";
        }).ToArray();
        string Omitted(int count) => count == 0 ? "" :
            $"- … {count} more server{(count == 1 ? "" : "s")}; find their tools with searchTools()";
        int SectionLength(int kept)
        {
            var lines = new List<string> { introduction };
            lines.AddRange(heads.Take(kept));
            if (kept < listed.Length) lines.Add(Omitted(listed.Length - kept));
            return string.Join('\n', lines).Length;
        }

        var kept = listed.Length;
        while (kept > 0 && SectionLength(kept) > maximumSectionLength) kept--;
        var summaryLimit = kept == 0 ? 0 : Math.Min(maximumSummaryLength,
            (maximumSectionLength - SectionLength(kept)) / kept - 2);
        var lines = new List<string> { introduction };
        for (var index = 0; index < kept; index++)
        {
            var server = listed[index];
            var summary = (server.Description?.Trim() ?? "");
            if (summary.Length == 0) summary = manager?.GetServerInstructions(server.Name)?.Trim() ?? "";
            summary = summary.Split(['\r', '\n'], 2)[0].Trim();
            if (summaryLimit <= 1) summary = "";
            else if (summary.Length > summaryLimit)
                summary = summary[..(summaryLimit - 1)].TrimEnd() + "…";
            lines.Add(summary.Length > 0 ? heads[index] + ": " + summary : heads[index]);
        }
        if (kept < listed.Length) lines.Add(Omitted(listed.Length - kept));
        return string.Join('\n', lines);
    }

    internal static IReadOnlyList<McpServerConfiguration> SelectEffectiveServers(
        IEnumerable<McpServerConfiguration> configuredServers,
        IEnumerable<ExtensionMcpServerRegistration> extensionServers)
    {
        var effective = configuredServers.ToList();
        var configuredNamespaces = effective.Select(server => McpToolIdentifiers.Namespace(server.Name))
            .ToHashSet(StringComparer.Ordinal);
        effective.AddRange(extensionServers
            .Where(server => !configuredNamespaces.Contains(McpToolIdentifiers.Namespace(server.Configuration.Name)))
            .Select(server => server.Configuration));
        return effective;
    }

    private static async Task<string?> ConnectServerAsync(McpServerConfiguration server,
        McpServerConnection connection, Action<McpServerConfiguration, McpServerConnection?> publish,
        CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(server.Timeout);
            await connection.ConnectAsync(deadline.Token);
            publish(server, connection);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            publish(server, connection);
            return "MCP server " + server.Name + " timed out.";
        }
        catch (McpSignInRequiredException error)
        {
            publish(server, connection);
            return server.AuthProvider is { } provider
                ? error.Message
                : "MCP server " + server.Name + " needs authorization. Run pisharp mcp login " + server.Name + ".";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Connection errors may contain server credentials or authorization headers.
            publish(server, connection);
            return "MCP server " + server.Name + " could not connect (" + error.GetType().Name + ").";
        }
    }

    private static HashSet<McpToolExposure> ConfiguredExposures(McpServerConfiguration server) =>
        [server.Exposure, .. server.ToolExposure.Values];

    private static bool HasIndirectTools(McpServerConfiguration server)
    {
        var exposures = ConfiguredExposures(server);
        return exposures.Contains(McpToolExposure.Codemode) ||
            exposures.Contains(McpToolExposure.CodemodeDeferred) || exposures.Contains(McpToolExposure.Deferred);
    }

    private static string? ReadAuthorizationScope(Uri authorizationUri)
    {
        foreach (var field in authorizationUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator < 0 || !Uri.UnescapeDataString(field[..separator].Replace('+', ' '))
                    .Equals("scope", StringComparison.Ordinal)) continue;
            var value = Uri.UnescapeDataString(field[(separator + 1)..].Replace('+', ' '));
            var scopes = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal);
            var result = string.Join(' ', scopes);
            return result.Length == 0 ? null : result;
        }
        return null;
    }


}
