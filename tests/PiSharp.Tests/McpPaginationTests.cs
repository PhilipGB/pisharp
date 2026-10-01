using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Mcp;

namespace PiSharp.Tests;

public sealed class McpPaginationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrNullCursorDoesNotRequestAnEmptyNextPage(bool nullCursor)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-pagination-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var origin = "http://127.0.0.1:" + port;
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin + "/");
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listRequests = 0;
        var server = Task.Run(async () =>
        {
            while (!deadline.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().WaitAsync(deadline.Token); }
                catch (Exception error) when (error is OperationCanceledException or HttpListenerException or
                    ObjectDisposedException)
                { return; }

                var response = context.Response;
                response.ContentType = "application/json";
                string body;
                if (context.Request.HttpMethod != "POST")
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    body = "{}";
                }
                else
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                    var requestText = await reader.ReadToEndAsync(deadline.Token);
                    if (string.IsNullOrWhiteSpace(requestText))
                    {
                        response.StatusCode = (int)HttpStatusCode.Accepted;
                        body = "";
                    }
                    else
                    {
                        using var message = JsonDocument.Parse(requestText);
                        var request = message.RootElement;
                        if (!request.TryGetProperty("id", out var id))
                        {
                            response.StatusCode = (int)HttpStatusCode.Accepted;
                            body = "";
                        }
                        else
                        {
                            var method = request.GetProperty("method").GetString();
                            object result = method switch
                            {
                                "server/discover" => new
                                {
                                    supportedVersions = new[] { "2025-11-25" },
                                    capabilities = new { }
                                },
                                "initialize" => new
                                {
                                    protocolVersion = request.GetProperty("params").GetProperty("protocolVersion").GetString(),
                                    capabilities = new { tools = new { } },
                                    serverInfo = new { name = "pagination-fixture", version = "1.0" }
                                },
                                "tools/list" => ToolPage(request, Interlocked.Increment(ref listRequests), nullCursor),
                                _ => new { }
                            };
                            body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result });
                        }
                    }
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, deadline.Token);
                response.Close();
            }
        }, deadline.Token);

        try
        {
            var configurationPath = Path.Combine(root, "mcp.json");
            await File.WriteAllTextAsync(configurationPath, JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["pagination"] = new { url = origin + "/mcp", exposure = "direct" }
                }
            }));
            var configuration = await McpConfiguration.LoadAsync(root, root, false);
            using var catalog = ExtensionCatalog.Load(root, root, false, discover: false);

            Assert.Empty(await McpRuntime.RegisterAsync(configuration, catalog, root, deadline.Token));

            Assert.Equal(1, Volatile.Read(ref listRequests));
            Assert.Contains(catalog.Registration.ToolDefinitions, tool =>
                tool.Function.Name == "mcp__pagination__first");
            Assert.DoesNotContain(catalog.Registration.ToolDefinitions, tool =>
                tool.Function.Name == "mcp__pagination__second");
        }
        finally
        {
            deadline.Cancel();
            listener.Close();
            try { await server; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or IOException) { }
            Directory.Delete(root, recursive: true);
        }
    }

    private static object ToolPage(JsonElement request, int page, bool nullCursor)
    {
        var cursor = request.GetProperty("params").TryGetProperty("cursor", out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var name = string.IsNullOrEmpty(cursor) ? page == 1 ? "first" : "second" : "second";
        return page == 1
            ? new
            {
                tools = new[] { new { name, description = name, inputSchema = new { type = "object" } } },
                nextCursor = nullCursor ? null : ""
            }
            : new { tools = new[] { new { name, description = name, inputSchema = new { type = "object" } } } };
    }
}
