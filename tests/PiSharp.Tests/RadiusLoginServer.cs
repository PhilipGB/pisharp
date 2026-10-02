using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PiSharp.Tests;

internal sealed class RadiusLoginServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _serve;
    private readonly bool _holdToken;
    public TaskCompletionSource TokenRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Origin { get; }

    public RadiusLoginServer(bool holdToken = false)
    {
        _holdToken = holdToken;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        Origin = "http://127.0.0.1:" + ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        _listener.Prefixes.Add(Origin + "/");
        _listener.Start();
        _serve = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                string body;
                switch (context.Request.Url!.AbsolutePath)
                {
                    case "/v1/oauth/device":
                        body = JsonSerializer.Serialize(new
                        {
                            device_code = "fixture-device",
                            user_code = "ABCD-1234",
                            verification_uri = Origin + "/pair",
                            expires_in = 600,
                            interval = 0
                        });
                        break;
                    case "/v1/oauth/token":
                        TokenRequested.TrySetResult();
                        if (_holdToken) await Task.Delay(Timeout.Infinite, _shutdown.Token);
                        body = """{"access_token":"fixture-radius-access","refresh_token":"fixture-radius-refresh","expires_in":3600}""";
                        break;
                    default:
                        context.Response.StatusCode = 404;
                        body = "{}";
                        break;
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token);
                context.Response.Close();
            }
        }
        catch (Exception error) when (_shutdown.IsCancellationRequested &&
            error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException)
        { }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Close();
        await _serve;
        _shutdown.Dispose();
    }
}
