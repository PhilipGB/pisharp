using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace PiSharp.Tests;

internal sealed class McpLifecycleHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
    private readonly Task _serve;
    private readonly bool _expireFirstToolCall;
    private readonly bool _transientFirstResourceRead;
    private readonly bool _transientFirstToolCall;
    private readonly Func<string?, bool>? _authorize;
    private readonly ConcurrentQueue<string?> _authorizationHeaders = new();
    private int _initializeCount;
    private int _toolCallCount;
    private int _resourceReadCount;
    private readonly List<string> _toolCallSessions = [];

    public McpLifecycleHttpServer(bool expireFirstToolCall = false, bool transientFirstResourceRead = false,
        bool transientFirstToolCall = false, Func<string?, bool>? authorize = null)
    {
        _expireFirstToolCall = expireFirstToolCall;
        _transientFirstResourceRead = transientFirstResourceRead;
        _transientFirstToolCall = transientFirstToolCall;
        _authorize = authorize;
        var (listener, port) = TestLoopbackListener.Start();
        _listener = listener;
        Endpoint = "http://127.0.0.1:" + port + "/mcp";
        _serve = ServeAsync();
    }

    public string Endpoint { get; }
    public int InitializeCount => Volatile.Read(ref _initializeCount);
    public int ToolCallCount => Volatile.Read(ref _toolCallCount);
    public int ResourceReadCount => Volatile.Read(ref _resourceReadCount);
    public IReadOnlyList<string?> AuthorizationHeaders => _authorizationHeaders.ToArray();
    public IReadOnlyList<string> ToolCallSessions
    {
        get { lock (_toolCallSessions) return _toolCallSessions.ToArray(); }
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or
                ObjectDisposedException)
            { return; }
            try { await HandleAsync(context); }
            catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException or
                OperationCanceledException)
            { }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var authorization = request.Headers["Authorization"];
        _authorizationHeaders.Enqueue(authorization);
        if (_authorize is not null && !_authorize(authorization))
        {
            await WriteAsync(response, (int)HttpStatusCode.Unauthorized, "{}");
            return;
        }
        if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != "/mcp")
        {
            await WriteAsync(response, 405, "{}");
            return;
        }

        using var body = await JsonDocument.ParseAsync(request.InputStream, cancellationToken: _stop.Token);
        var message = body.RootElement;
        var method = message.GetProperty("method").GetString();
        if (!message.TryGetProperty("id", out var id))
        {
            response.StatusCode = (int)HttpStatusCode.Accepted;
            response.ContentLength64 = 0;
            response.Close();
            return;
        }

        if (method == "server/discover")
        {
            await WriteJsonAsync(response, new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                error = new { code = -32601, message = "Method not found" }
            });
            return;
        }

        if (method == "initialize")
        {
            var session = Interlocked.Increment(ref _initializeCount);
            response.AddHeader("Mcp-Session-Id", "session-" + session);
            await WriteJsonAsync(response, new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                result = new
                {
                    protocolVersion = message.GetProperty("params").GetProperty("protocolVersion").GetString(),
                    capabilities = new { tools = new { }, resources = new { } },
                    serverInfo = new { name = "lifecycle-fixture", version = "1.0" }
                }
            });
            return;
        }

        if (method == "tools/list")
        {
            await WriteJsonAsync(response, new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                result = new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "echo",
                            description = "Echo a value.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new { value = new { type = "string" } },
                                required = new[] { "value" }
                            }
                        }
                    }
                }
            });
            return;
        }

        if (method == "tools/call")
        {
            var count = Interlocked.Increment(ref _toolCallCount);
            var session = request.Headers["Mcp-Session-Id"] ?? "(none)";
            lock (_toolCallSessions) _toolCallSessions.Add(session);
            if (_expireFirstToolCall && count == 1)
            {
                await WriteAsync(response, 404, "{}");
                return;
            }
            if (_transientFirstToolCall && count == 1)
            {
                await WriteAsync(response, 502, "{}");
                return;
            }
            var value = message.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString();
            await WriteJsonAsync(response, new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                result = new
                {
                    content = new[] { new { type = "text", text = "reconnected:" + value } },
                    isError = false
                }
            });
            return;
        }

        if (method == "resources/list")
        {
            await WriteJsonAsync(response, new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                result = new
                {
                    resources = new[] { new { uri = "fixture://note", name = "note", mimeType = "text/plain" } }
                }
            });
            return;
        }

        if (method == "resources/read")
        {
            var count = Interlocked.Increment(ref _resourceReadCount);
            if (_transientFirstResourceRead && count == 1)
            {
                await WriteAsync(response, 502, "{}");
                return;
            }
            await WriteJsonAsync(response, new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                result = new
                {
                    contents = new[]
                    {
                        new { uri = "fixture://note", mimeType = "text/plain", text = "fixture resource" }
                    }
                }
            });
            return;
        }

        await WriteJsonAsync(response, new
        {
            jsonrpc = "2.0",
            id = id.Clone(),
            error = new { code = -32601, message = "Method not found" }
        });
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value) =>
        await WriteAsync(response, 200, JsonSerializer.Serialize(value));

    private static async Task WriteAsync(HttpListenerResponse response, int statusCode, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        response.StatusCode = statusCode;
        if (statusCode is >= 200 and < 300) response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        try { await _serve; }
        catch (Exception error) when (error is OperationCanceledException or HttpListenerException or
            ObjectDisposedException)
        { }
        _stop.Dispose();
    }
}
