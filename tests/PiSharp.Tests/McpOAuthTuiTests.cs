using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class McpOAuthTuiTests
{
    [Fact]
    public async Task McpLoginUrlKeepsItsHyperlinkAndShortHintWhenWrappedInTheTui()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-oauth-tui-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        await using var server = new McpOAuthServer();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "mcp.json"), JsonSerializer.Serialize(new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["oauth"] = new
                    {
                        url = server.Origin + "/mcp",
                        oauth = new { clientId = "fixture-client" }
                    }
                }
            }));

            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"stty rows 24 cols 48; exec dotnet {ShellQuote(typeof(CliArguments).Assembly.Location)} --local --offline --no-session");
            start.ArgumentList.Add("/dev/null");
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            foreach (var name in new[] { "OPENAI_API_KEY", "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_SETTINGS_PATH" })
                start.Environment.Remove(name);
            process = Process.Start(start);
            Assert.NotNull(process);

            var output = new StringBuilder();
            var outputGate = new object();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var authorizationUrlVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationFrameVisible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var signedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = Task.Run(async () =>
            {
                var buffer = new char[4096];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer);
                    if (count == 0) break;
                    lock (outputGate)
                    {
                        output.Append(buffer, 0, count);
                        var current = output.ToString();
                        if (current.Contains("Qwen3.8-27B-GGUF", StringComparison.Ordinal)) ready.TrySetResult();
                        if (current.Contains(server.Origin + "/authorize?", StringComparison.Ordinal))
                        {
                            authorizationUrlVisible.TrySetResult();
                            var target = FindAuthorizationUrl(current, server.Origin);
                            if (target is not null && FindFrame(current, target, "Ctrl+click to open") is not null)
                                notificationFrameVisible.TrySetResult();
                        }
                        if (current.Contains("Signed in to MCP server oauth.", StringComparison.Ordinal))
                            signedIn.TrySetResult();
                    }
                }
                lock (outputGate) return output.ToString();
            });
            var stderr = process.StandardError.ReadToEndAsync();
            try { await ready.Task.WaitAsync(deadline.Token); }
            catch (OperationCanceledException error)
            {
                string partial;
                lock (outputGate) partial = output.ToString();
                var exited = process.HasExited;
                var exitCode = exited ? process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "running";
                var diagnostics = exited ? await stderr : "";
                throw new TimeoutException($"The TUI did not become ready (exited={exited}, code={exitCode}).\n{partial}\n{diagnostics}", error);
            }
            await process.StandardInput.WriteAsync("/mcp login oauth\n");
            await process.StandardInput.FlushAsync();
            await authorizationUrlVisible.Task.WaitAsync(deadline.Token);
            await notificationFrameVisible.Task.WaitAsync(deadline.Token);

            string captured;
            lock (outputGate) captured = output.ToString();
            var authorizationUrl = FindAuthorizationUrl(captured, server.Origin);
            Assert.NotNull(authorizationUrl);
            var url = new Uri(authorizationUrl!);
            var frame = FindFrame(captured, authorizationUrl!, "Ctrl+click to open");
            Assert.True(frame is not null, "No wrapped hyperlink notification frame was found.\n" +
                captured[^Math.Min(captured.Length, 5000)..]);
            Assert.True(CountOccurrences(frame!, Osc8Open(authorizationUrl!)) >= 2,
                "The wrapped authorization URL must reopen its OSC 8 target on each visible row.");
            Assert.Contains(Osc8Open(authorizationUrl!) + "Ctrl+click to open" + Osc8Close, frame!);

            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            var callbackUri = new UriBuilder(new Uri(query["redirect_uri"]!))
            {
                Query = "code=fixture-code&state=" + Uri.EscapeDataString(query["state"]!)
            }.Uri;
            using var browser = new HttpClient();
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(callbackUri, deadline.Token)).StatusCode);
            await signedIn.Task.WaitAsync(deadline.Token);

            await process.StandardInput.WriteAsync("/quit\n");
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            captured = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("MCP sign-in failed", captured, StringComparison.Ordinal);
            Assert.DoesNotContain("Error:", await stderr, StringComparison.Ordinal);
        }
        finally
        {
            deadline.Cancel();
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string? FindAuthorizationUrl(string output, string origin)
    {
        const string marker = "\u001b]8;;";
        var searchFrom = 0;
        while ((searchFrom = output.IndexOf(marker, searchFrom, StringComparison.Ordinal)) >= 0)
        {
            var targetStart = searchFrom + marker.Length;
            var targetEnd = output.IndexOf("\u001b\\", targetStart, StringComparison.Ordinal);
            if (targetEnd < 0) return null;
            var target = output[targetStart..targetEnd];
            if (target.StartsWith(origin + "/authorize?", StringComparison.Ordinal)) return target;
            searchFrom = targetEnd + 2;
        }
        return null;
    }

    private static string? FindFrame(string output, string target, string hint)
    {
        const string frameStart = "\u001b[?2026h";
        const string frameEnd = "\u001b[?2026l";
        var open = Osc8Open(target);
        var searchFrom = 0;
        while ((searchFrom = output.IndexOf(frameStart, searchFrom, StringComparison.Ordinal)) >= 0)
        {
            var start = searchFrom + frameStart.Length;
            var end = output.IndexOf(frameEnd, start, StringComparison.Ordinal);
            if (end < 0) return null;
            var frame = output[start..end];
            if (frame.Contains(open, StringComparison.Ordinal) &&
                frame.Contains(open + hint + Osc8Close, StringComparison.Ordinal)) return frame;
            searchFrom = end + frameEnd.Length;
        }
        return null;
    }

    private static string Osc8Open(string target) => "\u001b]8;;" + target + "\u001b\\";
    private const string Osc8Close = "\u001b]8;;\u001b\\";

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
            index += value.Length) count++;
        return count;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed class McpOAuthServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromSeconds(40));
        private readonly Task _serve;

        public string Origin { get; }

        public McpOAuthServer()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Origin = "http://127.0.0.1:" + port;
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _serve = ServeAsync();
        }

        private async Task ServeAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token); }
                catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException)
                { return; }

                try { await HandleAsync(context); }
                catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException)
                {
                    try { context.Response.Abort(); }
                    catch (HttpListenerException) { }
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            response.ContentType = "application/json";
            string body;
            if (request.Url!.AbsolutePath.Contains("oauth-protected-resource", StringComparison.Ordinal))
            {
                body = JsonSerializer.Serialize(new
                {
                    resource = Origin + "/mcp",
                    authorization_servers = new[] { Origin }
                });
            }
            else if (request.Url.AbsolutePath.Contains("oauth-authorization-server", StringComparison.Ordinal))
            {
                body = JsonSerializer.Serialize(new
                {
                    issuer = Origin,
                    authorization_endpoint = Origin + "/authorize",
                    token_endpoint = Origin + "/token",
                    response_types_supported = new[] { "code" },
                    token_endpoint_auth_methods_supported = new[] { "none" },
                    code_challenge_methods_supported = new[] { "S256" },
                    authorization_response_iss_parameter_supported = false
                });
            }
            else if (request.Url.AbsolutePath == "/token")
            {
                body = """{"access_token":"fixture-access","refresh_token":"fixture-refresh","token_type":"Bearer","expires_in":3600}""";
            }
            else if (request.Url.AbsolutePath == "/mcp")
            {
                if (!string.Equals(request.Headers["Authorization"], "Bearer fixture-access", StringComparison.Ordinal))
                {
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    response.AddHeader("WWW-Authenticate", "Bearer resource_metadata=\"" + Origin +
                        "/.well-known/oauth-protected-resource/mcp\"");
                    body = "{}";
                }
                else
                {
                    using var input = new StreamReader(request.InputStream, request.ContentEncoding);
                    var text = await input.ReadToEndAsync(_shutdown.Token);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        response.StatusCode = (int)HttpStatusCode.Accepted;
                        body = "";
                    }
                    else
                    {
                        using var message = JsonDocument.Parse(text);
                        var root = message.RootElement;
                        if (!root.TryGetProperty("id", out var id))
                        {
                            response.StatusCode = (int)HttpStatusCode.Accepted;
                            body = "";
                        }
                        else
                        {
                            var method = root.GetProperty("method").GetString();
                            object result = method switch
                            {
                                "server/discover" => new
                                {
                                    supportedVersions = new[] { "2025-11-25" },
                                    capabilities = new { }
                                },
                                "initialize" => new
                                {
                                    protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                                    capabilities = new { tools = new { } },
                                    serverInfo = new { name = "oauth-tui-fixture", version = "1.0" }
                                },
                                "tools/list" => new { tools = Array.Empty<object>() },
                                _ => new { }
                            };
                            body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result });
                        }
                    }
                }
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                body = "{}";
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, _shutdown.Token);
            response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Close();
            try { await _serve; }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or IOException) { }
            _shutdown.Dispose();
        }
    }
}
