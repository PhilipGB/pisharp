using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PiSharp.Tests;

internal sealed class McpOAuthRecoveryServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
    private readonly Task _serve;
    private readonly ConcurrentBag<Task> _requests = [];
    private readonly ConcurrentQueue<string> _executed = new();
    private readonly ConcurrentQueue<string> _tokenForms = new();
    private int _unauthorized;
    private int _refreshes;
    private int _initializations;
    private readonly int? _tokenLifetime;
    private string _requiredAccess = "new";

    public McpOAuthRecoveryServer(int? tokenLifetime = 3600)
    {
        _tokenLifetime = tokenLifetime;
        var (listener, port) = TestLoopbackListener.Start();
        _listener = listener;
        Origin = "http://127.0.0.1:" + port;
        _serve = ServeAsync();
    }

    public string Origin { get; }
    public Uri Endpoint => new(Origin + "/mcp");
    public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource AllUnauthorized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Refreshes => Volatile.Read(ref _refreshes);
    public int Initializations => Volatile.Read(ref _initializations);
    public string[] Executed => _executed.ToArray();
    public string[] TokenForms => _tokenForms.ToArray();
    public void ExpireAccessToken() => Volatile.Write(ref _requiredAccess, "new-" + (Refreshes + 1));

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _requests.Add(HandleAsync(await _listener.GetContextAsync().WaitAsync(_stop.Token)));
        }
        catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url!.AbsolutePath;
            if (context.Request.HttpMethod == "GET" && path.Contains("oauth-protected-resource", StringComparison.Ordinal))
            {
                await WriteAsync(context.Response, new { resource = Endpoint.AbsoluteUri, authorization_servers = new[] { Origin } });
                return;
            }
            if (context.Request.HttpMethod == "GET" && path.Contains("oauth-authorization-server", StringComparison.Ordinal))
            {
                await WriteAsync(context.Response, new
                {
                    issuer = Origin,
                    authorization_endpoint = Origin + "/authorize",
                    token_endpoint = Origin + "/token",
                    response_types_supported = new[] { "code" },
                    grant_types_supported = new[] { "authorization_code", "refresh_token" },
                    code_challenge_methods_supported = new[] { "S256" }
                });
                return;
            }
            if (path == "/token")
            {
                using var reader = new StreamReader(context.Request.InputStream);
                _tokenForms.Enqueue(await reader.ReadToEndAsync(_stop.Token));
                var issued = Interlocked.Increment(ref _refreshes);
                RefreshStarted.TrySetResult();
                await ReleaseRefresh.Task.WaitAsync(_stop.Token);
                await WriteAsync(context.Response, new
                {
                    access_token = issued == 1 ? "new" : "new-" + issued,
                    refresh_token = issued == 1 ? "refresh-new" : "refresh-new-" + issued,
                    token_type = "Bearer",
                    expires_in = _tokenLifetime
                });
                return;
            }
            if (context.Request.HttpMethod != "POST" || path != "/mcp")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }
            using var document = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: _stop.Token);
            var message = document.RootElement;
            if (!message.TryGetProperty("id", out var id))
            {
                context.Response.StatusCode = 202;
                context.Response.Close();
                return;
            }
            var method = message.GetProperty("method").GetString();
            if (method == "tools/call" && context.Request.Headers["Authorization"] != "Bearer " + Volatile.Read(ref _requiredAccess))
            {
                if (Interlocked.Increment(ref _unauthorized) >= 3) AllUnauthorized.TrySetResult();
                context.Response.StatusCode = 401;
                context.Response.AddHeader("WWW-Authenticate", "Bearer resource_metadata=\"" + Origin + "/.well-known/oauth-protected-resource/mcp\"");
                context.Response.Close();
                return;
            }
            object result;
            switch (method)
            {
                case "initialize":
                    Interlocked.Increment(ref _initializations);
                    result = new
                    {
                        protocolVersion = message.GetProperty("params").GetProperty("protocolVersion").GetString(),
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "oauth-recovery", version = "1" }
                    };
                    break;
                case "tools/list":
                    result = new
                    {
                        tools = new[] { new { name = "echo", description = "Echo", inputSchema = new
                        {
                            type = "object", properties = new { value = new { type = "string" } }, required = new[] { "value" }
                        } } }
                    };
                    break;
                case "tools/call":
                    var value = message.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString()!;
                    _executed.Enqueue(value);
                    result = new { content = new[] { new { type = "text", text = value } } };
                    break;
                default:
                    await WriteAsync(context.Response, new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Method not found" } });
                    return;
            }
            await WriteAsync(context.Response, new { jsonrpc = "2.0", id = id.Clone(), result });
        }
        catch (Exception error) when (error is OperationCanceledException or HttpListenerException or IOException or ObjectDisposedException) { }
    }

    private static async Task WriteAsync(HttpListenerResponse response, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseRefresh.TrySetResult();
        _stop.Cancel();
        _listener.Close();
        await _serve;
        await Task.WhenAll(_requests);
        _stop.Dispose();
    }
}
