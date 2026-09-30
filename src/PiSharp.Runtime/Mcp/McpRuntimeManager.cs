namespace PiSharp.Runtime.Mcp;

/// <summary>Session-scoped MCP status and management actions exposed by the built-in /mcp command.</summary>
public sealed class McpRuntimeManager(string? agentDirectory = null)
{
    private sealed class ServerEntry(McpServerConfiguration configuration,
        Func<McpServerConfiguration, McpServerConnection>? createConnection,
        Action<McpServerConfiguration, McpServerConnection?>? publish)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public McpServerConfiguration Configuration = configuration;
        public McpServerConnection? Connection;
        public Func<McpServerConfiguration, McpServerConnection>? CreateConnection = createConnection;
        public Action<McpServerConfiguration, McpServerConnection?>? Publish = publish;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ServerEntry> _servers = new(StringComparer.Ordinal);

    internal void Register(McpServerConfiguration configuration, McpServerConnection? connection,
        Func<McpServerConfiguration, McpServerConnection>? createConnection = null,
        Action<McpServerConfiguration, McpServerConnection?>? publish = null)
    {
        lock (_gate)
        {
            if (!_servers.TryGetValue(configuration.Name, out var entry))
                _servers.Add(configuration.Name, entry = new(configuration, createConnection, publish));
            entry.Configuration = configuration;
            entry.Connection = connection;
            entry.CreateConnection = createConnection ?? entry.CreateConnection;
            entry.Publish = publish ?? entry.Publish;
        }
    }

    public async Task<string> HandleCommandAsync(string arguments, CancellationToken cancellationToken)
    {
        var parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts is ["status"])
            return FormatStatus();
        if (parts is ["reconnect", var reconnectName])
            return await ReconnectAsync(reconnectName, cancellationToken);
        if (parts is ["enable", var enableName])
            return await SetEnabledAsync(enableName, true, cancellationToken);
        if (parts is ["disable", var disableName])
            return await SetEnabledAsync(disableName, false, cancellationToken);
        if (parts is ["exposure", var exposureName, var exposureValue] &&
            Enum.TryParse<McpToolExposure>(exposureValue.Replace("-", "", StringComparison.Ordinal), true,
                out var exposure))
            return await SetExposureAsync(exposureName, exposure, cancellationToken);
        if (parts is ["login", var loginName])
            return await SignInAsync(loginName, cancellationToken);
        if (parts is ["logout", var logoutName])
            return await SignOutAsync(logoutName, cancellationToken);
        return "Usage: /mcp [status | reconnect <server> | enable <server> | disable <server> | exposure <server> <mode> | login <server> | logout <server>]";
    }

    private async Task<string> ReconnectAsync(string name, CancellationToken cancellationToken)
    {
        var entry = Find(name);
        if (entry is null) return "Unknown MCP server: " + name;
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!entry.Configuration.Enabled || entry.Connection is null)
                return "MCP server " + name + " is disabled or unavailable.";
            try
            {
                await entry.Connection.ReconnectAsync(cancellationToken);
                entry.Publish?.Invoke(entry.Configuration, entry.Connection);
                return "Reconnected MCP server " + name + ".";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                entry.Publish?.Invoke(entry.Configuration, entry.Connection);
                return "Could not reconnect MCP server " + name + " (" + error.GetType().Name + ").";
            }
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<string> SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        var entry = Find(name);
        if (entry is null) return "Unknown MCP server: " + name;
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Configuration.Enabled == enabled)
                return "MCP server " + name + " is already " + (enabled ? "enabled." : "disabled.");
            if (enabled && entry.CreateConnection is null)
                return "MCP server " + name + " cannot be started by this runtime.";
            var updated = entry.Configuration with { Enabled = enabled };
            if (!await SaveAsync(entry, enabled, null, cancellationToken))
                return "MCP server " + name + " configuration no longer exists.";
            var previous = entry.Connection;
            entry.Configuration = updated;
            if (!enabled)
            {
                entry.Connection = null;
                entry.Publish?.Invoke(updated, null);
                if (previous is not null) await previous.DisposeAsync();
                return "Disabled MCP server " + name + ".";
            }

            var connection = entry.CreateConnection!(updated);
            entry.Connection = connection;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(updated.Timeout);
                await connection.ConnectAsync(deadline.Token);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                entry.Publish?.Invoke(updated, connection);
                return "Enabled MCP server " + name + " but connection failed (" + error.GetType().Name + ").";
            }
            entry.Publish?.Invoke(updated, connection);
            return "Enabled MCP server " + name + ".";
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<string> SetExposureAsync(string name, McpToolExposure exposure,
        CancellationToken cancellationToken)
    {
        var entry = Find(name);
        if (entry is null) return "Unknown MCP server: " + name;
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Configuration.Exposure == exposure)
                return "MCP server " + name + " already uses " + ExposureName(exposure) + " exposure.";
            var updated = entry.Configuration with { Exposure = exposure };
            if (!await SaveAsync(entry, null, exposure, cancellationToken))
                return "MCP server " + name + " configuration no longer exists.";
            entry.Configuration = updated;
            entry.Publish?.Invoke(updated, entry.Connection);
            return "Set MCP server " + name + " exposure to " + ExposureName(exposure) + ".";
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<string> SignInAsync(string name, CancellationToken cancellationToken)
    {
        var entry = Find(name);
        if (entry is null) return "Unknown MCP server: " + name;
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (agentDirectory is null) return "MCP OAuth sign-in is unavailable.";
            if (entry.Configuration.Url is null || HasAuthorizationHeader(entry.Configuration))
                return "MCP server " + name + " does not use OAuth.";
            if (!entry.Configuration.Enabled || entry.Connection is null)
                return "Enable MCP server " + name + " before signing in.";
            try
            {
                await McpOAuthLogin.SignInAsync(entry.Configuration, agentDirectory, Console.Out,
                    openBrowser: !Console.IsInputRedirected, TimeSpan.FromMinutes(5), cancellationToken);
                await entry.Connection.ReconnectAsync(cancellationToken);
                entry.Publish?.Invoke(entry.Configuration, entry.Connection);
                return "Signed in to MCP server " + name + ".";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                return "MCP sign-in failed (" + error.GetType().Name + ").";
            }
        }
        finally { entry.Gate.Release(); }
    }

    private async Task<string> SignOutAsync(string name, CancellationToken cancellationToken)
    {
        var entry = Find(name);
        if (entry is null) return "Unknown MCP server: " + name;
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (agentDirectory is null) return "MCP OAuth sign-out is unavailable.";
            if (entry.Configuration.Url is null || HasAuthorizationHeader(entry.Configuration))
                return "MCP server " + name + " does not use OAuth.";
            var removed = await new McpTokenCache(agentDirectory).RemoveAsync(entry.Configuration.Url, cancellationToken);
            if (entry.Connection is not null)
            {
                try { await entry.Connection.ReconnectAsync(cancellationToken); }
                catch (McpSignInRequiredException) { }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
                entry.Publish?.Invoke(entry.Configuration, entry.Connection);
            }
            return removed ? "Signed out of MCP server " + name + "." : "No stored credentials for MCP server " + name + ".";
        }
        finally { entry.Gate.Release(); }
    }

    private static bool HasAuthorizationHeader(McpServerConfiguration configuration) =>
        configuration.Headers.Keys.Any(key => key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));

    private static string ExposureName(McpToolExposure exposure) => exposure switch
    {
        McpToolExposure.CodemodeDeferred => "codemode-deferred",
        _ => exposure.ToString().ToLowerInvariant()
    };

    private async Task<bool> SaveAsync(ServerEntry entry, bool? enabled,
        McpToolExposure? exposure, CancellationToken cancellationToken)
    {
        if (entry.Configuration.Scope == "extension") return true;
        return await McpConfigurationEditor.UpdateAsync(entry.Configuration.SourcePath,
            entry.Configuration.Name, enabled, exposure, cancellationToken);
    }

    private ServerEntry? Find(string name)
    {
        lock (_gate) return _servers.TryGetValue(name, out var entry) ? entry : null;
    }

    internal string? GetServerInstructions(string name) => Find(name)?.Connection?.Instructions;

    private string FormatStatus()
    {
        ServerEntry[] servers;
        lock (_gate) servers = _servers.Values.OrderBy(item => item.Configuration.Name, StringComparer.Ordinal).ToArray();
        if (servers.Length == 0) return "No MCP servers configured.";
        return string.Join(Environment.NewLine, servers.Select(item =>
        {
            var state = !item.Configuration.Enabled ? "disabled" : item.Connection?.State ?? "unavailable";
            var tools = item.Connection?.Tools.Count ?? 0;
            var line = item.Configuration.Name + ": " + state + " (" + ExposureName(item.Configuration.Exposure)
                + ", " + item.Configuration.Scope + ", " + tools + " tools)";
            return item.Connection?.Error is { } error ? line + " — " + error : line;
        }));
    }
}
