using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PiSharp.Runtime.Mcp;

/// <summary>Owns SDK transports, authentication, protocol negotiation and discovered-resource refresh lifetime.</summary>
internal static class McpClientConnection
{
    private const string s_protocolVersion = "2025-11-25";
    private static readonly HashSet<string> s_protocolVersions = new(StringComparer.Ordinal)
        { s_protocolVersion, "2025-06-18", "2025-03-26", "2024-11-05" };

    public static async Task<McpConnectedServer> OpenAsync(McpServerConfiguration server,
        string agentDirectory, string workingDirectory, Action<Uri>? authorizationRequired,
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
            IClientTransport CreateTransport()
            {
                if (server.Command is { } command)
                {
                    var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
                    foreach (var (key, value) in server.Environment) environment[key] = Expand(value);
                    return new StdioClientTransport(new StdioClientTransportOptions
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
                    return new HttpClientTransport(options,
                        new HttpClient(new McpProtocolCompatibilityHandler(server.Url!,
                            new McpProviderAuthHandler(server.Name, providerAuth, providerTokenResolver))),
                        ownsHttpClient: true);
                else if (oauth is null)
                    return new HttpClientTransport(options,
                        new HttpClient(new McpProtocolCompatibilityHandler(server.Url!, new HttpClientHandler())),
                        ownsHttpClient: true);
                var tokenCache = new McpTokenCache(agentDirectory).ForServerWithRefresh(server.Name, server.Url!);
                options.OAuth = oauth.CreateOptions(server.Url!, tokenCache,
                    oauth.CallbackUrl ?? new Uri("http://127.0.0.1:38119/callback"),
                    (context, _) =>
                    {
                        authorizationRequired?.Invoke(context.AuthorizationUri);
                        throw new McpSignInRequiredException();
                    });
                refreshHandler = new McpOAuthRefreshHandler(tokenCache);
                HttpMessageHandler handler = refreshHandler;
                if (oauth.AuthServerMetadataUrl is { } metadataUrl)
                    handler = new McpOAuthMetadataHandler(server.Url!, metadataUrl, handler);
                handler = new McpProtocolCompatibilityHandler(server.Url!, handler);
                return new HttpClientTransport(options, new HttpClient(handler), ownsHttpClient: true);
            }

            transport = CreateTransport();
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

            var attemptedProtocolVersions = new HashSet<string>(StringComparer.Ordinal);
            var requestedProtocolVersion = s_protocolVersion;
            while (client is null)
            {
                attemptedProtocolVersions.Add(requestedProtocolVersion);
                try
                {
                    client = await McpClient.CreateAsync(transport, new McpClientOptions
                    {
                        ProtocolVersion = requestedProtocolVersion,
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
                }
                catch (ModelContextProtocol.McpException error)
                {
                    var serverProtocolVersion = GetMismatchedProtocolVersion(error.Message);
                    if (serverProtocolVersion is null || !s_protocolVersions.Contains(serverProtocolVersion) ||
                        !attemptedProtocolVersions.Add(serverProtocolVersion))
                        throw;

                    await ((IAsyncDisposable)transport).DisposeAsync();
                    requestedProtocolVersion = serverProtocolVersion;
                    transport = CreateTransport();
                }
            }
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

    private static string? GetMismatchedProtocolVersion(string message)
    {
        var match = Regex.Match(message, @"^Server protocol version mismatch\. Expected [^,]+, got (?<version>\S+)$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["version"].Value : null;
    }

}
