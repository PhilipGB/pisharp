using System.Net;
using System.Text;

namespace PiSharp.Core.Models.OAuth;

/// <summary>The code/state pair delivered to the callback server.</summary>
public sealed record CallbackResult(string Code, string State);

/// <summary>
/// Local HTTP callback server for browser OAuth flows (pinned pi-ai: the node http
/// callback servers). Binds 127.0.0.1 on the given port, serves the single expected
/// path, and resolves with the code/state (or null when the manual input path wins).
/// </summary>
public sealed class CallbackServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly HttpListener _listener;
    private readonly TaskCompletionSource<CallbackResult?> _tcs;
    private readonly Task<CallbackResult?> _waitForCode;
    private readonly string _callbackPath;
    private readonly string _expectedState;
    private readonly string _appLabel;
    private volatile bool _settled;

    /// <summary>Starts the callback server and resolves the server info once listening.</summary>
    public static async Task<CallbackServer> StartAsync(
        int port,
        string callbackPath,
        string expectedState,
        string appLabel,
        CancellationToken signal = default)
    {
        var server = new CallbackServer(port, callbackPath, expectedState, appLabel);
        await server.StartListeningAsync(signal);
        return server;
    }

    private CallbackServer(int port, string callbackPath, string expectedState, string appLabel)
    {
        _port = port;
        _callbackPath = callbackPath;
        _expectedState = expectedState;
        _appLabel = appLabel;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _tcs = new TaskCompletionSource<CallbackResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waitForCode = _tcs.Task;
        _ = Task.Run(() => AcceptLoopAsync(_tcs), CancellationToken.None);
    }

    private async Task StartListeningAsync(CancellationToken signal)
    {
        try
        {
            _listener.Start();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Failed to start OAuth callback server on port {_port}: {exception.Message}",
                exception);
        }

        signal.ThrowIfCancellationRequested();
    }

    private async Task AcceptLoopAsync(TaskCompletionSource<CallbackResult?> tcs)
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return; // listener closed
            }

            _ = Task.Run(() => HandleRequestAsync(context, tcs));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, TaskCompletionSource<CallbackResult?> tcs)
    {
        try
        {
            var url = new Uri(context.Request.Url!.AbsoluteUri);
            if (url.AbsolutePath != _callbackPath)
            {
                await WriteResponseAsync(context.Response, 404, OAuthHtml.Error($"{_appLabel} callback route not found."));
                return;
            }

            var parts = url.Query.TrimStart('?').Split('&');
            var codeParam = parts.FirstOrDefault(part => part.StartsWith("code=", StringComparison.Ordinal));
            var stateParam = parts.FirstOrDefault(part => part.StartsWith("state=", StringComparison.Ordinal));
            var errorParam = parts.FirstOrDefault(part => part.StartsWith("error=", StringComparison.Ordinal));
            var code = codeParam is not null ? codeParam["code=".Length..] : null;
            var state = stateParam is not null ? stateParam["state=".Length..] : null;
            var error = errorParam is not null ? errorParam["error=".Length..] : null;

            if (!string.IsNullOrEmpty(error))
            {
                await WriteResponseAsync(context.Response, 400, OAuthHtml.Error($"{_appLabel} authentication did not complete.", $"Error: {error}"));
                return;
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                await WriteResponseAsync(context.Response, 400, OAuthHtml.Error("Missing code or state parameter."));
                return;
            }

            if (_expectedState.Length > 0 && state != _expectedState)
            {
                await WriteResponseAsync(context.Response, 400, OAuthHtml.Error("State mismatch."));
                return;
            }

            await WriteResponseAsync(
                context.Response,
                200,
                OAuthHtml.Success($"{_appLabel} authentication completed. You can close this window."));
            if (!_settled)
            {
                _settled = true;
                tcs.TrySetResult(new CallbackResult(code, state));
            }
        }
        catch
        {
            try
            {
                await WriteResponseAsync(context.Response, 500, "Internal error");
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, int status, string html)
    {
        response.StatusCode = status;
        response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(html);
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>
    /// Waits for the browser callback; resolves null when CancelWait is called (the
    /// manual input path won the race).
    /// </summary>
    public Task<CallbackResult?> WaitForCodeAsync() => _waitForCode;

    /// <summary>Settles the wait with null (manual input won).</summary>
    public void CancelWait()
    {
        if (!_settled)
        {
            _settled = true;
            _tcs.TrySetResult(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Best effort.
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// Parses user-pasted authorization input (a full redirect URL, a "code#state"
/// fragment, a query string, or a bare code) into code/state (pinned pi-ai:
/// parseAuthorizationInput).
/// </summary>
public static class AuthorizationInputParser
{
    /// <summary>Parsed code/state from user input.</summary>
    public sealed record ParsedInput(string? Code, string? State);

    public static ParsedInput Parse(string input)
    {
        var value = input.Trim();
        if (value.Length == 0)
        {
            return new ParsedInput(null, null);
        }

        try
        {
            var url = new Uri(value);
            var query = System.Web.HttpUtility.ParseQueryString(url.Query.TrimStart('?'));
            return new ParsedInput(
                string.IsNullOrEmpty(query["code"]) ? null : query["code"],
                string.IsNullOrEmpty(query["state"]) ? null : query["state"]);
        }
        catch (UriFormatException)
        {
            // Not a URL.
        }

        if (value.Contains('#', StringComparison.Ordinal))
        {
            var parts = value.Split('#', 2);
            return new ParsedInput(parts[0], parts.Length > 1 ? parts[1] : null);
        }

        if (value.Contains("code=", StringComparison.Ordinal))
        {
            var query = System.Web.HttpUtility.ParseQueryString(value.TrimStart('?'));
            return new ParsedInput(
                string.IsNullOrEmpty(query["code"]) ? null : query["code"],
                string.IsNullOrEmpty(query["state"]) ? null : query["state"]);
        }

        return new ParsedInput(value, null);
    }
}

/// <summary>Minimal HTML pages for the OAuth callback (pinned pi-ai: oauth-page.ts).</summary>
public static class OAuthHtml
{
    public static string Success(string message) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Success</title></head>" +
        $"<body style=\"font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#f6f8fa\">" +
        $"<div style=\"text-align:center\"><h1 style=\"color:#1a7f37\">{message}</h1></div></body></html>";

    public static string Error(string title, string detail = "") =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Authentication error</title></head>" +
        $"<body style=\"font-family:sans-serif;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;background:#faf2f2\">" +
        $"<div style=\"text-align:center\"><h1 style=\"color:#cf222e\">{title}</h1>" +
        (detail.Length > 0 ? $"<p>{detail}</p>" : string.Empty) + "</div></body></html>";
}
