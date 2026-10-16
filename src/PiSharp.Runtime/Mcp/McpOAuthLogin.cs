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
        TextWriter output, bool openBrowser, TimeSpan timeout, CancellationToken cancellationToken = default,
        Action<string, Uri>? authorizationUrlPresenter = null, string? requestedScope = null,
        TextReader? manualCallbackInput = null)
    {
        if (server.Url is null || server.Headers.Keys.Any(key =>
            key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("MCP server does not use OAuth.");
        var settings = server.OAuth is { } value ? McpOAuthSettings.Parse(value) :
            new McpOAuthSettings(null, null, null, []);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var cimdIdentity = await McpOAuthCimdDiscovery.ResolveAsync(server.Url, settings, deadline.Token)
            .ConfigureAwait(false);
        var callbackBase = settings.CallbackUrl;
        if (callbackBase is null)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            callbackBase = new Uri("http://127.0.0.1:" + port + "/callback");
        }
        var callback = McpOAuthCimdDiscovery.ApplyCallbackPath(callbackBase, cimdIdentity);
        manualCallbackInput ??= authorizationUrlPresenter is null && ReferenceEquals(output, Console.Out)
            ? Console.In : null;
        using var listener = new HttpListener();
        listener.Prefixes.Add(callback.GetLeftPart(UriPartial.Authority) + "/");
        listener.Start();
        var authorizationStarted = false;
        var tokenCache = new McpTokenCache(agentDirectory).ForServerWithRefresh(server.Name, server.Url);
        var existingTokens = await tokenCache.GetTokensAsync(deadline.Token);
        var options = settings.CreateOptions(server.Url, tokenCache, callback,
            async (context, token) =>
            {
                authorizationStarted = true;
                if (authorizationUrlPresenter is null)
                    await output.WriteLineAsync("Sign in to MCP server \"" + server.Name + "\" in your browser:\n" +
                        context.AuthorizationUri.AbsoluteUri);
                else authorizationUrlPresenter(server.Name, context.AuthorizationUri);
                if (manualCallbackInput is not null && authorizationUrlPresenter is null)
                    await output.WriteLineAsync("If the browser cannot reach this machine, paste the full redirect URL here:");
                if (openBrowser)
                    try
                    {
                        Process.Start(new ProcessStartInfo(context.AuthorizationUri.AbsoluteUri)
                        { UseShellExecute = true });
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                    { /* The printed URL remains available for a manual browser. */ }
                Task<string?>? manualLine = manualCallbackInput?.ReadLineAsync(token).AsTask();
                var callbackTask = listener.GetContextAsync().WaitAsync(token);
                while (true)
                {
                    if (manualLine is not null && await Task.WhenAny(callbackTask, manualLine).ConfigureAwait(false) == manualLine)
                    {
                        var pasted = await manualLine.ConfigureAwait(false);
                        if (pasted is null) manualLine = null;
                        else
                        {
                            manualLine = manualCallbackInput!.ReadLineAsync(token).AsTask();
                            if (TryParseManualCallback(pasted, context.RedirectUri, out var manualResult))
                                return manualResult;
                            if (authorizationUrlPresenter is null)
                                await output.WriteLineAsync("The redirect URL does not match this sign-in. Paste the full redirect URL from the browser address bar.");
                        }
                        continue;
                    }
                    var request = await callbackTask.ConfigureAwait(false);
                    callbackTask = listener.GetContextAsync().WaitAsync(token);
                    var uri = request.Request.Url;
                    var expectedRedirect = context.RedirectUri;
                    var accepted = uri is not null && SameCallbackOriginAndPath(uri, expectedRedirect) &&
                        SameCallbackHostHeader(request.Request.Headers["Host"], expectedRedirect) &&
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
            }, cimdIdentity?.ClientMetadataDocumentUri);
        if (cimdIdentity is null && options.ClientId is null && existingTokens?.ClientId is { } cachedClientId)
        {
            options.ClientId = cachedClientId;
            options.ClientSecret = existingTokens.ClientSecret;
        }
        ITokenCache registrationTokenCache = cimdIdentity is null ? tokenCache :
            new McpOAuthCimdTokenCache(tokenCache, cimdIdentity.ClientMetadataDocumentUri);
        var signInTokenCache = new SignInTokenCache(registrationTokenCache);
        options.TokenCache = signInTokenCache;
        if (!string.IsNullOrWhiteSpace(requestedScope))
        {
            var requiredScopes = requestedScope.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            options.ScopeSelector = scope => (scope ?? Array.Empty<string>())
                .Concat(requiredScopes).Distinct(StringComparer.Ordinal);
        }
        var transportOptions = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = server.Url,
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = options,
            ConnectionTimeout = server.Timeout
        };
        var refreshHandler = new McpOAuthRefreshHandler(tokenCache);
        HttpMessageHandler handler = refreshHandler;
        if (settings.AuthServerMetadataUrl is { } metadataUrl)
            handler = new McpOAuthMetadataHandler(server.Url, metadataUrl, handler);
        handler = new McpProtocolCompatibilityHandler(server.Url, handler);
        var transport = new HttpClientTransport(transportOptions,
            new HttpClient(handler), ownsHttpClient: true);
        McpClient? client = null;
        try
        {
            client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
            await output.WriteLineAsync((authorizationStarted ? "Signed in to" : "Already connected to") +
                " MCP server \"" + server.Name + "\".");
        }
        catch (Exception error) when (signInTokenCache.HasStoredTokens &&
            (error is HttpRequestException or ModelContextProtocol.McpException))
        {
            await output.WriteLineAsync("Signed in to MCP server \"" + server.Name + "\".");
        }
        finally
        {
            await refreshHandler.WaitForSettledAsync();
            if (client is not null) await client.DisposeAsync();
            await transport.DisposeAsync();
        }
    }

    private static bool SameCallbackOriginAndPath(Uri actual, Uri expected) =>
        actual.Scheme.Equals(expected.Scheme, StringComparison.OrdinalIgnoreCase) &&
        actual.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase) && actual.Port == expected.Port &&
        actual.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(actual.UserInfo) && string.IsNullOrEmpty(actual.Fragment);

    private static bool SameCallbackHostHeader(string? actual, Uri expected) =>
        actual is not null && actual.Equals(expected.Authority, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseManualCallback(string value, Uri expected, out AuthorizationResult result)
    {
        result = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var actual) ||
            !SameCallbackOriginAndPath(actual, expected)) return false;
        var query = System.Web.HttpUtility.ParseQueryString(actual.Query);
        if (string.IsNullOrEmpty(query["code"]) || string.IsNullOrEmpty(query["state"])) return false;
        result = new AuthorizationResult { Code = query["code"], State = query["state"], Iss = query["iss"] };
        return true;
    }

    private sealed class SignInTokenCache(ITokenCache inner) : ITokenCache
    {
        private int _hasStoredTokens;

        public bool HasStoredTokens => Volatile.Read(ref _hasStoredTokens) != 0;

        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken) =>
            HasStoredTokens ? inner.GetTokensAsync(cancellationToken) : ValueTask.FromResult<TokenContainer?>(null);

        public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            await inner.StoreTokensAsync(tokens, cancellationToken);
            Volatile.Write(ref _hasStoredTokens, 1);
        }
    }
}
