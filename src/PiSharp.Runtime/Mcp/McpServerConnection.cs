using System.Net.Http;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PiSharp.Runtime.Mcp;

internal sealed class McpConnectedServer(McpClient client, McpOAuthRefreshHandler? refreshHandler,
    IList<McpClientTool> tools, bool hasResources, IList<McpClientResource>? resources = null,
    IList<McpClientResourceTemplate>? resourceTemplates = null, SemaphoreSlim? toolsRefreshGate = null,
    SemaphoreSlim? resourcesRefreshGate = null) : IAsyncDisposable
{
    private readonly object _gate = new();
    private IList<McpClientTool> _tools = tools;
    private IList<McpClientResource> _resources = resources ?? [];
    private IList<McpClientResourceTemplate> _resourceTemplates = resourceTemplates ?? [];
    private readonly SemaphoreSlim _toolsRefreshGate = toolsRefreshGate ?? new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _resourcesRefreshGate = resourcesRefreshGate ?? new SemaphoreSlim(1, 1);
    private readonly HashSet<Task> _refreshTasks = [];
    private int _disposed;

    public McpClient Client { get; } = client;
    public string? Instructions => Client.ServerInstructions;
    public IList<McpClientTool> Tools { get { lock (_gate) return _tools.ToArray(); } }
    public IList<McpClientResource> Resources { get { lock (_gate) return _resources.ToArray(); } }
    public IList<McpClientResourceTemplate> ResourceTemplates { get { lock (_gate) return _resourceTemplates.ToArray(); } }
    public bool HasResources { get; } = hasResources;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void ReplaceTools(IList<McpClientTool> tools)
    {
        lock (_gate) _tools = tools.ToArray();
    }

    public void ReplaceResources(IList<McpClientResource> resources,
        IList<McpClientResourceTemplate> resourceTemplates)
    {
        lock (_gate)
        {
            _resources = resources.ToArray();
            _resourceTemplates = resourceTemplates.ToArray();
        }
    }

    public void TrackRefresh(Func<Task> refresh)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        Task task;
        lock (_gate)
        {
            if (IsDisposed) return;
            task = Task.Run(async () =>
            {
                try { await refresh(); }
                catch (Exception) { }
            });
            _refreshTasks.Add(task);
        }
        _ = task.ContinueWith(completed =>
        {
            lock (_gate) _refreshTasks.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (refreshHandler is not null) await refreshHandler.WaitForSettledAsync();
        await Client.DisposeAsync();
        Task[] refreshTasks;
        lock (_gate) refreshTasks = _refreshTasks.ToArray();
        try { await Task.WhenAll(refreshTasks); }
        catch (Exception) { }
        _toolsRefreshGate.Dispose();
        _resourcesRefreshGate.Dispose();
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
    public IList<McpClientTool> Tools => Volatile.Read(ref _connected)?.Tools ?? [];
    public string? Instructions => Volatile.Read(ref _connected)?.Instructions;
    public bool HasResources => Volatile.Read(ref _connected)?.HasResources == true;

    public bool IsCurrent(McpClient client) =>
        ReferenceEquals(Volatile.Read(ref _connected)?.Client, client);

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
