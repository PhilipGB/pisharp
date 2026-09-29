using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace PiSharp.Runtime.Mcp;

/// <summary>Runs the SDK authorization-code flow only after an explicit MCP login command.</summary>
public static class McpOAuthLogin
{
    public static async Task SignInAsync(McpServerConfiguration server, string agentDirectory,
        TextWriter output, bool openBrowser, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (server.Url is null || server.Headers.Keys.Any(key =>
            key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("MCP server does not use OAuth.");
        var settings = server.OAuth is { } value ? McpOAuthSettings.Parse(value) :
            new McpOAuthSettings(null, null, null, []);
        var callback = settings.CallbackUrl;
        if (callback is null)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            callback = new Uri("http://127.0.0.1:" + port + "/callback");
        }
        using var listener = new HttpListener();
        listener.Prefixes.Add(callback.GetLeftPart(UriPartial.Authority) + "/");
        listener.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var authorizationStarted = false;
        var options = settings.CreateOptions(server.Url, new McpTokenCache(agentDirectory), callback,
            async (context, token) =>
            {
                authorizationStarted = true;
                await output.WriteLineAsync("Sign in to MCP server \"" + server.Name + "\" in your browser:\n" +
                    context.AuthorizationUri.AbsoluteUri);
                if (openBrowser)
                    try
                    {
                        Process.Start(new ProcessStartInfo(context.AuthorizationUri.AbsoluteUri)
                        { UseShellExecute = true });
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                    { /* The printed URL remains available for a manual browser. */ }
                while (true)
                {
                    var request = await listener.GetContextAsync().WaitAsync(token);
                    var uri = request.Request.Url;
                    var expectedPath = callback.AbsolutePath;
                    var accepted = uri is not null && uri.AbsolutePath == expectedPath &&
                        request.Request.RemoteEndPoint is { } remote && IPAddress.IsLoopback(remote.Address);
                    var response = request.Response;
                    response.ContentType = "text/html; charset=utf-8";
                    var body = Encoding.UTF8.GetBytes(accepted
                        ? "<!doctype html><title>MCP sign-in</title><p>You may close this page.</p>"
                        : "<!doctype html><title>MCP sign-in</title><p>Invalid callback.</p>");
                    response.StatusCode = accepted ? 200 : 400;
                    response.ContentLength64 = body.Length;
                    await response.OutputStream.WriteAsync(body, token);
                    response.Close();
                    if (!accepted) continue;
                    if (uri!.Query.Contains("error=", StringComparison.Ordinal))
                        throw new InvalidOperationException("MCP authorization was denied.");
                    return new AuthorizationResult
                    {
                        Code = request.Request.QueryString["code"],
                        State = request.Request.QueryString["state"],
                        Iss = request.Request.QueryString["iss"]
                    };
                }
            });
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = server.Url,
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = options,
            ConnectionTimeout = server.Timeout
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        await output.WriteLineAsync((authorizationStarted ? "Signed in to" : "Already connected to") +
            " MCP server \"" + server.Name + "\".");
    }
}
