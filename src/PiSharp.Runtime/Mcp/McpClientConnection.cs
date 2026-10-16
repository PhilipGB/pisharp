using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PiSharp.Runtime.Mcp;

/// <summary>Owns SDK transports, authentication, protocol negotiation and tool-discovery refresh lifetime.</summary>
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
        using var oauthRequest = McpOAuthRequestContext.Begin();
        McpClient? client = null;
        IClientTransport? transport = null;
        McpOAuthRefreshHandler? refreshHandler = null;
        McpConnectedServer? connected = null;
        var pendingToolsRefresh = 0;
        var toolsRefreshGate = new SemaphoreSlim(1, 1);
        try
        {
            var configuredOAuth = server.AuthProvider is not null || server.Headers.Keys.Any(key =>
                key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) ? null :
                (server.OAuth is { } configured ? McpOAuthSettings.Parse(configured) :
                    new McpOAuthSettings(null, null, null, []));
            var cimdIdentity = configuredOAuth is { ClientRegistration: "cimd" } && server.Url is { } endpoint
                ? await McpOAuthCimdDiscovery.ResolveAsync(endpoint, configuredOAuth, cancellationToken)
                    .ConfigureAwait(false)
                : null;
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

                var oauth = configuredOAuth;
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
                ModelContextProtocol.Authentication.ITokenCache oauthTokenCache = cimdIdentity is null ? tokenCache :
                    new McpOAuthCimdTokenCache(tokenCache, cimdIdentity.ClientMetadataDocumentUri);
                var callbackPath = cimdIdentity is { IssuerResponses: false } ? "/callback/" + cimdIdentity.Id : "/callback";
                var redirectUri = oauth.CallbackUrl ?? new Uri("http://127.0.0.1:38119" + callbackPath);
                options.OAuth = oauth.CreateOptions(server.Url!, oauthTokenCache,
                    redirectUri,
                    (context, _) =>
                    {
                        authorizationRequired?.Invoke(context.AuthorizationUri);
                        throw new McpSignInRequiredException();
                    }, cimdIdentity?.ClientMetadataDocumentUri);
                refreshHandler = new McpOAuthRefreshHandler(tokenCache);
                HttpMessageHandler handler = new McpSharedOAuthRefreshHandler(tokenCache, refreshHandler);
                if (oauth.AuthServerMetadataUrl is { } metadataUrl)
                    handler = new McpOAuthMetadataHandler(server.Url!, metadataUrl, handler);
                handler = new McpProtocolCompatibilityHandler(server.Url!, handler);
                return new HttpClientTransport(options, new HttpClient(handler), ownsHttpClient: true);
            }

            transport = CreateTransport();
            async ValueTask RefreshToolsAsync(CancellationToken token)
            {
                using var refreshRequest = McpOAuthRequestContext.Begin();
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
                                ["notifications/tools/list_changed"] = (_, token) => QueueToolsRefreshAsync(token)
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
            connected = new McpConnectedServer(client, refreshHandler, tools, hasResources, toolsRefreshGate);
            if (Interlocked.Exchange(ref pendingToolsRefresh, 0) != 0)
                await RefreshToolsAsync(cancellationToken);
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
            }
            throw;
        }
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
