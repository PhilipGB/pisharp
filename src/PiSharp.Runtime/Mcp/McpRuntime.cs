using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
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
        ExtensionCatalog catalog, string workingDirectory, CancellationToken cancellationToken = default) =>
        RegisterAsync(configuration, catalog, workingDirectory, cancellationToken, manager: null,
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
        var resourceGate = new object();
        var resourceToolsGate = new object();
        var resourceServers = new Dictionary<string,
            (string Name, McpServerConnection Connection, TimeSpan Timeout, ToolExposure Exposure)>(StringComparer.Ordinal);
        var resourceOwner = "mcp:resources";
        var resourceNames = new HashSet<string>(StringComparer.Ordinal)
            { "list_mcp_resources", "list_mcp_resource_templates", "read_mcp_resource" };

        void RefreshResourceTools()
        {
            lock (resourceToolsGate)
            {
                var owned = registration.GetOwnedToolNames(resourceOwner).ToHashSet(StringComparer.Ordinal);
                if (registration.ToolDefinitions.Any(tool => resourceNames.Contains(tool.Function.Name) &&
                        !owned.Contains(tool.Function.Name)))
                    return;
                (string Name, McpServerConnection Connection, TimeSpan Timeout, ToolExposure Exposure)[] current;
                lock (resourceGate) current = resourceServers.Values.ToArray();
                var source = new ResourceSourceInfo("builtin:mcp", "builtin", "builtin", "top-level", null);
                if (current.Length == 0)
                {
                    registration.ReplaceOwnedTools(resourceOwner, [], source);
                    return;
                }
                new McpResourceTools(current).Configure(registration);
            }
        }

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
                        RegisterServerTools(updated, published, registration, published.Tools, published.Instructions);

                    lock (resourceGate)
                    {
                        if (published is not null && updated.Enabled && published.HasResources &&
                            updated.Exposure != McpToolExposure.Hidden)
                            resourceServers[updated.Name] = (updated.Name, published, updated.Timeout,
                                MapExposure(updated.Exposure));
                        else resourceServers.Remove(updated.Name);
                    }
                    RefreshResourceTools();
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
                        return OpenServerAsync(openingConfiguration, configuration.AgentDirectory, workingDirectory,
                            providerTokenResolver,
                            (client, tools) =>
                            {
                                lock (publishGate)
                                {
                                    if (created?.IsCurrent(client) != true) return Task.CompletedTask;
                                    var activeConfiguration = currentServer;
                                    if (activeConfiguration.Enabled)
                                        RegisterServerTools(activeConfiguration, created, registration, tools,
                                            client.ServerInstructions);
                                    else
                                        registration.ReplaceOwnedTools("mcp:" + activeConfiguration.Name, [],
                                            new ResourceSourceInfo("builtin:mcp", "builtin", "builtin", "top-level", null));
                                    lock (resourceGate)
                                    {
                                        if (activeConfiguration.Enabled && created.HasResources &&
                                            activeConfiguration.Exposure != McpToolExposure.Hidden)
                                            resourceServers[activeConfiguration.Name] = (activeConfiguration.Name, created,
                                                activeConfiguration.Timeout, MapExposure(activeConfiguration.Exposure));
                                        else resourceServers.Remove(activeConfiguration.Name);
                                    }
                                    RefreshResourceTools();
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

        var ownedResourceNames = registration.GetOwnedToolNames(resourceOwner).ToHashSet(StringComparer.Ordinal);
        var hasResourceServers = false;
        lock (resourceGate) hasResourceServers = resourceServers.Count > 0;
        if (hasResourceServers && registration.ToolDefinitions.Any(tool =>
                resourceNames.Contains(tool.Function.Name) && !ownedResourceNames.Contains(tool.Function.Name)))
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

    private static void RegisterServerTools(McpServerConfiguration server, McpServerConnection connection,
        ExtensionRegistration registration, IList<McpClientTool> tools, string? instructions)
    {
        var owner = "mcp:" + server.Name;
        var ownedNames = registration.GetOwnedToolNames(owner).ToHashSet(StringComparer.Ordinal);
        var taken = registration.ToolDefinitions.Select(item => item.Function.Name)
            .Where(name => !ownedNames.Contains(name)).ToHashSet(StringComparer.Ordinal);
        var definitions = new List<PiSharpToolRegistration>();
        var renderers = new Dictionary<string, PiSharpToolRenderer>(StringComparer.Ordinal);
        var toolNames = McpToolIdentifiers.CreateNames(server.Name, tools.Select(tool => tool.Name).ToArray(), taken);
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            var name = toolNames[index];
            taken.Add(name);
            var exposure = server.ExposureFor(tool.Name);
            var function = new McpToolFunction(tool, connection, name, server.Timeout);
            definitions.Add(new PiSharpToolRegistration(function, MapExposure(exposure),
                DefaultActive: exposure == McpToolExposure.Direct,
                Namespace: new PiSharpToolNamespace(McpToolIdentifiers.Namespace(server.Name),
                    server.Description?.Trim(), instructions?.Trim()),
                AllowNestedInvocation: exposure is McpToolExposure.Codemode or McpToolExposure.CodemodeDeferred,
                OutputSchema: tool.ProtocolTool.OutputSchema));
            var label = server.Name + "/" + tool.Name;
            renderers.Add(name, new PiSharpToolRenderer(
                renderCall: (arguments, context) =>
                    PiSharpToolCallDisplay.Format(label, arguments, context.IsExpanded),
                renderResult: RenderResult));
        }
        var source = new ResourceSourceInfo("builtin:mcp", "builtin", "builtin", "top-level", null);
        registration.ReplaceOwnedTools(owner, definitions, source, renderers);
    }

    private static async Task<McpConnectedServer> OpenServerAsync(McpServerConfiguration server,
        string agentDirectory, string workingDirectory,
        Func<string, CancellationToken, Task<string?>>? providerTokenResolver,
        Func<McpClient, IList<McpClientTool>, Task> onToolsChanged, CancellationToken cancellationToken)
    {
        McpClient? client = null;
        IClientTransport? transport = null;
        McpOAuthRefreshHandler? refreshHandler = null;
        McpConnectedServer? connected = null;
        var pendingToolsRefresh = 0;
        var pendingResourcesRefresh = 0;
        var toolsRefreshGate = new SemaphoreSlim(1, 1);
        var resourcesRefreshGate = new SemaphoreSlim(1, 1);
        try
        {
            if (server.Command is { } command)
            {
                var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
                foreach (var (key, value) in server.Environment) environment[key] = Expand(value);
                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = Expand(command),
                    Arguments = server.Arguments.Select(Expand).ToArray(),
                    WorkingDirectory = server.WorkingDirectory is null ? workingDirectory :
                        Path.GetFullPath(Expand(server.WorkingDirectory), workingDirectory),
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = environment
                });
            }
            else
            {
                var oauth = server.AuthProvider is not null || server.Headers.Keys.Any(key =>
                    key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) ? null :
                    (server.OAuth is { } configured ? McpOAuthSettings.Parse(configured) :
                        new McpOAuthSettings(null, null, null, []));
                var options = new HttpClientTransportOptions
                {
                    Name = server.Name,
                    Endpoint = server.Url!,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = server.Headers.ToDictionary(item => item.Key,
                        item => Expand(item.Value), StringComparer.Ordinal),
                    ConnectionTimeout = server.Timeout
                };
                if (server.AuthProvider is { } providerAuth)
                    transport = new HttpClientTransport(options,
                        new HttpClient(new McpProtocolCompatibilityHandler(server.Url!,
                            new McpProviderAuthHandler(server.Name, providerAuth, providerTokenResolver))),
                        ownsHttpClient: true);
                else if (oauth is null)
                    transport = new HttpClientTransport(options,
                        new HttpClient(new McpProtocolCompatibilityHandler(server.Url!, new HttpClientHandler())),
                        ownsHttpClient: true);
                else
                {
                    var tokenCache = new McpTokenCache(agentDirectory).ForServerWithRefresh(server.Name, server.Url!);
                    options.OAuth = oauth.CreateOptions(server.Url!, tokenCache,
                        oauth.CallbackUrl ?? new Uri("http://127.0.0.1:38119/callback"),
                        (_, _) => throw new McpSignInRequiredException());
                    refreshHandler = new McpOAuthRefreshHandler(tokenCache);
                    HttpMessageHandler handler = refreshHandler;
                    if (oauth.AuthServerMetadataUrl is { } metadataUrl)
                        handler = new McpOAuthMetadataHandler(server.Url!, metadataUrl, handler);
                    handler = new McpProtocolCompatibilityHandler(server.Url!, handler);
                    transport = new HttpClientTransport(options, new HttpClient(handler), ownsHttpClient: true);
                }
            }
            async ValueTask RefreshToolsAsync(CancellationToken token)
            {
                var current = connected;
                var activeClient = client;
                if (current is null || activeClient is null || current.IsDisposed)
                {
                    Interlocked.Exchange(ref pendingToolsRefresh, 1);
                    return;
                }
                await toolsRefreshGate.WaitAsync(token);
                try
                {
                    if (current.IsDisposed) return;
                    var refreshed = activeClient.ServerCapabilities.Tools is null
                        ? []
                        : await activeClient.ListToolsAsync(cancellationToken: token);
                    if (current.IsDisposed) return;
                    current.ReplaceTools(refreshed);
                    await onToolsChanged(activeClient, refreshed);
                }
                finally { toolsRefreshGate.Release(); }
            }

            async ValueTask RefreshResourcesAsync(CancellationToken token)
            {
                var current = connected;
                var activeClient = client;
                if (current is null || activeClient is null || current.IsDisposed)
                {
                    Interlocked.Exchange(ref pendingResourcesRefresh, 1);
                    return;
                }
                await resourcesRefreshGate.WaitAsync(token);
                try
                {
                    if (current.IsDisposed || activeClient.ServerCapabilities.Resources is null) return;
                    var resourcesTask = TryListResourcesAsync(activeClient, token);
                    var templatesTask = TryListResourceTemplatesAsync(activeClient, token);
                    await Task.WhenAll(resourcesTask, templatesTask);
                    if (!current.IsDisposed)
                        current.ReplaceResources(await resourcesTask, await templatesTask);
                }
                finally { resourcesRefreshGate.Release(); }
            }

            ValueTask QueueToolsRefreshAsync(CancellationToken _)
            {
                var current = connected;
                if (current is null)
                    Interlocked.Exchange(ref pendingToolsRefresh, 1);
                else current.TrackRefresh(async () =>
                {
                    using var deadline = new CancellationTokenSource(server.Timeout);
                    await RefreshToolsAsync(deadline.Token);
                });
                return ValueTask.CompletedTask;
            }

            ValueTask QueueResourcesRefreshAsync(CancellationToken _)
            {
                var current = connected;
                if (current is null)
                    Interlocked.Exchange(ref pendingResourcesRefresh, 1);
                else current.TrackRefresh(async () =>
                {
                    using var deadline = new CancellationTokenSource(server.Timeout);
                    await RefreshResourcesAsync(deadline.Token);
                });
                return ValueTask.CompletedTask;
            }

            client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                DiscoverProbeTimeout = TimeSpan.FromMilliseconds(750),
                Handlers = new McpClientHandlers
                {
                    NotificationHandlers = new Dictionary<string,
                        Func<JsonRpcNotification, CancellationToken, ValueTask>>(StringComparer.Ordinal)
                    {
                        ["notifications/tools/list_changed"] = (_, token) => QueueToolsRefreshAsync(token),
                        ["notifications/resources/list_changed"] = (_, token) => QueueResourcesRefreshAsync(token)
                    }
                }
            }, cancellationToken: cancellationToken);
            var tools = client.ServerCapabilities.Tools is null
                ? []
                : await client.ListToolsAsync(cancellationToken: cancellationToken);
            var hasResources = client.ServerCapabilities.Resources is not null;
            var resources = hasResources ? await TryListResourcesAsync(client, cancellationToken) : [];
            var templates = hasResources ? await TryListResourceTemplatesAsync(client, cancellationToken) : [];
            connected = new McpConnectedServer(client, refreshHandler, tools, hasResources, resources, templates,
                toolsRefreshGate, resourcesRefreshGate);
            if (Interlocked.Exchange(ref pendingToolsRefresh, 0) != 0)
                await RefreshToolsAsync(cancellationToken);
            if (Interlocked.Exchange(ref pendingResourcesRefresh, 0) != 0)
                await RefreshResourcesAsync(cancellationToken);
            return connected;
        }
        catch
        {
            if (connected is not null) await connected.DisposeAsync();
            else
            {
                if (refreshHandler is not null) await refreshHandler.WaitForSettledAsync();
                if (client is not null) await client.DisposeAsync();
                else if (transport is IAsyncDisposable disposable) await disposable.DisposeAsync();
                toolsRefreshGate.Dispose();
                resourcesRefreshGate.Dispose();
            }
            throw;
        }
    }

    private static async Task<IList<McpClientResource>> TryListResourcesAsync(McpClient client,
        CancellationToken cancellationToken)
    {
        try { return await client.ListResourcesAsync(cancellationToken: cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { return []; }
    }

    private static async Task<IList<McpClientResourceTemplate>> TryListResourceTemplatesAsync(McpClient client,
        CancellationToken cancellationToken)
    {
        try { return await client.ListResourceTemplatesAsync(cancellationToken: cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { return []; }
    }

    private static string Expand(string value) => Regex.Replace(value, @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", match =>
        Environment.GetEnvironmentVariable(match.Groups[1].Value) ??
        throw new InvalidOperationException("Missing environment variable " + match.Groups[1].Value + "."));

    private static ToolExposure MapExposure(McpToolExposure value) => value switch
    {
        McpToolExposure.Direct => ToolExposure.Direct,
        McpToolExposure.Deferred => ToolExposure.Deferred,
        McpToolExposure.Codemode or McpToolExposure.CodemodeDeferred => ToolExposure.CodeMode,
        _ => ToolExposure.Hidden
    };

    private static PiSharpToolRenderView RenderResult(PiSharpToolRenderResult result,
        PiSharpToolRenderContext context)
    {
        var output = (result.IsError ? result.Error ?? result.Text : result.Text)?.Trim();
        if (string.IsNullOrEmpty(output))
            return PiSharpToolRenderView.FromText("← (no output)", PiSharpToolTextStyle.Muted);
        var lines = output.Replace("\t", "    ", StringComparison.Ordinal).Split('\n');
        var shown = context.IsExpanded ? lines : lines.Take(5).ToArray();
        var spans = new List<PiSharpToolTextSpan>
        {
            new("← ", PiSharpToolTextStyle.Muted),
            new(string.Join('\n', shown), result.IsError ? PiSharpToolTextStyle.Error : PiSharpToolTextStyle.Output)
        };
        if (shown.Length < lines.Length)
            spans.Add(new($"\n... ({lines.Length - shown.Length} more lines; expand to view)",
                PiSharpToolTextStyle.Muted));
        return new(spans);
    }
}

internal sealed class McpToolFunction(McpClientTool tool, McpServerConnection connection, string name,
    TimeSpan timeout) : DelegatingAIFunction(tool)
{
    public override string Name => name;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var values = arguments.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var result = await connection.CallToolAsync(tool.Name, values, deadline.Token);
        var lines = new List<string>();
        var images = new List<PiSharpToolImage>();
        foreach (var content in result.Content)
            switch (content)
            {
                case TextContentBlock text:
                    lines.Add(text.Text);
                    break;
                case ImageContentBlock image:
                    images.Add(new PiSharpToolImage(image.MimeType, Convert.ToBase64String(image.DecodedData.Span)));
                    break;
                default:
                    lines.Add(JsonSerializer.Serialize(content));
                    break;
            }
        var output = string.Join("\n", lines);
        return new PiSharpToolResult(output, StructuredContent: result.StructuredContent,
            IsError: result.IsError == true, Error: result.IsError == true ? output : null, Images: images);
    }
}
