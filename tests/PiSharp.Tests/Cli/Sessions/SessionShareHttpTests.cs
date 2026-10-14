using System.Net;
using System.Text;
using PiSharp.Cli.Sessions;

namespace PiSharp.Tests;

public sealed class SessionShareHttpTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    public async Task RadiusUsesTheRealHttpBoundaryAndNeverInvokesGist(string outcome)
    {
        using var fixture = new SessionShareFixture();
        var (listener, port) = TestLoopbackListener.Start();
        using (listener)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
        using (var stopServer = new CancellationTokenSource())
        using (var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }))
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const string body = "{\"type\":\"session\",\"title\":\"héllo 💡\"}\n";
            var server = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                try
                {
                    Assert.Equal("POST", context.Request.HttpMethod);
                    Assert.Equal("/v1/artifacts?visibility=organization&title=Pi+session", context.Request.RawUrl);
                    Assert.Equal("Bearer fixture-token", context.Request.Headers["Authorization"]);
                    Assert.Equal("application/x-ndjson", context.Request.ContentType);
                    Assert.Equal(Encoding.UTF8.GetByteCount(body), context.Request.ContentLength64);
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    Assert.Equal(body, await reader.ReadToEndAsync(deadline.Token));
                    entered.TrySetResult();
                    if (outcome == "cancelled")
                    {
                        try { await Task.Delay(Timeout.Infinite, stopServer.Token); }
                        catch (OperationCanceledException) when (stopServer.IsCancellationRequested) { }
                        return;
                    }
                    context.Response.StatusCode = outcome == "success" ? 200 : 403;
                    context.Response.ContentType = "application/json";
                    var response = Encoding.UTF8.GetBytes(outcome == "success"
                        ? "{\"artifact\":{\"canonical_url\":\"https://radius.example/wire\"}}"
                        : "{\"error\":\"wire rejection\"}");
                    context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response, deadline.Token);
                }
                finally { context.Response.Close(); }
            });
            string? exportPath = null;
            var share = new SessionShare(http, new Uri($"http://127.0.0.1:{port}/"), _ => Task.FromResult<string?>("fixture-token"),
                (path, ct) => { exportPath = path; return File.WriteAllTextAsync(path, body, ct); },
                (_, _) => throw new InvalidOperationException("unexpected gist fallback"), fixture.GitHubPath);
            try
            {
                var pending = share.ShareAsync("auto", cancellationToken: cancellation.Token);
                await entered.Task.WaitAsync(deadline.Token);
                if (outcome == "cancelled") cancellation.Cancel();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(outcome switch
                {
                    "success" => new SessionShareResult("Share URL: https://radius.example/wire", ShareUrl: "https://radius.example/wire"),
                    "rejected" => new SessionShareResult("Failed to upload Radius artifact: wire rejection", true),
                    _ => new SessionShareResult("Share cancelled")
                }, result);
                Assert.Empty(fixture.Commands);
                Assert.False(Directory.Exists(Path.GetDirectoryName(exportPath!)));
            }
            finally
            {
                stopServer.Cancel();
                await server.WaitAsync(deadline.Token);
            }
        }
    }
}
