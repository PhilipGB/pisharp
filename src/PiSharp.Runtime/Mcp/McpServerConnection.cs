using System.Net.Http;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PiSharp.Runtime.Mcp;

internal sealed class McpConnectedServer(McpClient client, McpOAuthRefreshHandler? refreshHandler,
    IList<McpClientTool> tools, bool hasResources) : IAsyncDisposable
{
    public McpClient Client { get; } = client;
    public IList<McpClientTool> Tools { get; } = tools;
    public bool HasResources { get; } = hasResources;

    public async ValueTask DisposeAsync()
    {
        if (refreshHandler is not null) await refreshHandler.WaitForSettledAsync();
        await Client.DisposeAsync();
    }
}

/// <summary>Owns one MCP server connection and recovers on a later call after a transport drop.</summary>
internal sealed class McpServerConnection(string name, bool retryTransientConnects,
    Func<CancellationToken, Task<McpConnectedServer>> open)
    : IAsyncDisposable
{
    private static readonly TimeSpan[] s_connectRetryDelays =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];
    private static readonly TimeSpan s_readRetryDelay = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly List<McpConnectedServer> _retired = [];
    private McpConnectedServer? _connected;
    private int _disposed;

    public string Name => name;
    public string State { get; private set; } = "connecting";
    public string? Error { get; private set; }
    public IList<McpClientTool> Tools => _connected?.Tools ?? [];
    public bool HasResources => _connected?.HasResources == true;

    public async Task ConnectAsync(CancellationToken cancellationToken = default) =>
        _ = await GetConnectedAsync(cancellationToken);

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        McpConnectedServer? current;
        await _transition.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            current = _connected;
            _connected = null;
            if (current is not null) State = "disconnected";
        }
        finally { _transition.Release(); }
        if (current is not null) await current.DisposeAsync();
        _ = await GetConnectedAsync(cancellationToken);
    }

    public async Task<CallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var connected = await GetConnectedAsync(cancellationToken);
            try
            {
                var tool = connected.Tools.FirstOrDefault(item => item.Name == name) ??
                    throw new InvalidOperationException("MCP server no longer advertises tool " + name + ".");
                return await tool.CallAsync(arguments, cancellationToken: cancellationToken);
            }
            catch (Exception error) when (attempt == 0 && IsSessionExpired(error))
            {
                await RetireIfCurrentAsync(connected);
            }
            catch (Exception error) when (IsConnectionLost(error))
            {
                await DropIfCurrentAsync(connected);
                throw;
            }
        }
    }

    public Task<IList<McpClientResource>> ListResourcesAsync(CancellationToken cancellationToken) =>
        ExecuteReadAsync<IList<McpClientResource>>((client, token) => client.ListResourcesAsync(cancellationToken: token), cancellationToken);

    public Task<IList<McpClientResourceTemplate>> ListResourceTemplatesAsync(CancellationToken cancellationToken) =>
        ExecuteReadAsync<IList<McpClientResourceTemplate>>((client, token) => client.ListResourceTemplatesAsync(cancellationToken: token), cancellationToken);

    public Task<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken) =>
        ExecuteReadAsync<ReadResourceResult>((client, token) => client.ReadResourceAsync(uri, cancellationToken: token), cancellationToken);

    private async Task<T> ExecuteReadAsync<T>(Func<McpClient, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var connected = await GetConnectedAsync(cancellationToken);
            try { return await action(connected.Client, cancellationToken); }
            catch (Exception error) when (attempt == 0 && IsSessionExpired(error))
            {
                await RetireIfCurrentAsync(connected);
            }
            catch (Exception error) when (attempt == 0 && IsTransientHttpResponse(error))
            {
                await Task.Delay(s_readRetryDelay, cancellationToken);
            }
            catch (Exception error) when (IsConnectionLost(error))
            {
                await DropIfCurrentAsync(connected);
                throw;
            }
        }
    }

    private async Task<McpConnectedServer> GetConnectedAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_connected is not null) return _connected;
            State = "connecting";
            Error = null;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _connected = await open(cancellationToken);
                    State = "connected";
                    Error = null;
                    return _connected;
                }
                catch (Exception error) when (retryTransientConnects && attempt < s_connectRetryDelays.Length &&
                    IsTransient(error) && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(s_connectRetryDelays[attempt], cancellationToken);
                }
                catch (McpSignInRequiredException)
                {
                    State = "needs-auth";
                    Error = null;
                    throw;
                }
                catch (Exception error)
                {
                    State = "failed";
                    Error = error.GetType().Name;
                    throw;
                }
            }
        }
        finally { _transition.Release(); }
    }

    private async Task DropIfCurrentAsync(McpConnectedServer connected)
    {
        McpConnectedServer? dropped = null;
        await _transition.WaitAsync(CancellationToken.None);
        try
        {
            if (ReferenceEquals(_connected, connected))
            {
                _connected = null;
                dropped = connected;
                State = Volatile.Read(ref _disposed) == 0 ? "disconnected" : "closed";
                Error = "Connection closed";
            }
        }
        finally { _transition.Release(); }
        if (dropped is not null) await dropped.DisposeAsync();
    }

    private async Task RetireIfCurrentAsync(McpConnectedServer connected)
    {
        await _transition.WaitAsync(CancellationToken.None);
        try
        {
            if (ReferenceEquals(_connected, connected))
            {
                _connected = null;
                _retired.Add(connected);
                State = Volatile.Read(ref _disposed) == 0 ? "disconnected" : "closed";
                Error = "MCP session expired";
            }
        }
        finally { _transition.Release(); }
    }

    private static bool IsTransient(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is ClientTransportClosedException or IOException) return true;
            if (current is HttpRequestException http)
                return http.StatusCode is null || (int)http.StatusCode == 408 ||
                    (int)http.StatusCode == 429 || (int)http.StatusCode >= 500 && (int)http.StatusCode != 501;
        }
        return false;
    }

    private bool IsSessionExpired(Exception error)
    {
        if (!retryTransientConnects) return false;
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is HttpRequestException { StatusCode: { } status } && (int)status == 404) return true;
        return false;
    }

    private static bool IsTransientHttpResponse(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is HttpRequestException { StatusCode: { } status } &&
                ((int)status is 408 or 429 || (int)status >= 500 && (int)status != 501)) return true;
        return false;
    }

    private static bool IsConnectionLost(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is ClientTransportClosedException or IOException or ObjectDisposedException) return true;
            if (current is HttpRequestException { StatusCode: null }) return true;
            if (current is HttpRequestException { StatusCode: { } status } && (int)status == 404) return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        McpConnectedServer? connected;
        McpConnectedServer[] retired;
        await _transition.WaitAsync();
        try
        {
            connected = _connected;
            _connected = null;
            retired = _retired.ToArray();
            _retired.Clear();
            State = "closed";
        }
        finally { _transition.Release(); }
        if (connected is not null) await connected.DisposeAsync();
        foreach (var session in retired) await session.DisposeAsync();
    }
}
