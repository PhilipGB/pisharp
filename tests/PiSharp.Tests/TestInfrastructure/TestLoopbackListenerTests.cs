using System.Net;
using System.Net.Sockets;

namespace PiSharp.Tests;

public sealed class TestLoopbackListenerTests
{
    [Fact]
    public async Task ConcurrentStartsReserveDistinctLoopbackPorts()
    {
        var starts = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => TestLoopbackListener.Start())));
        try
        {
            Assert.Equal(12, starts.Select(start => start.Port).Distinct().Count());
        }
        finally
        {
            foreach (var (listener, _) in starts) listener.Close();
        }
    }

    [Fact]
    public async Task OccupiedFirstPortRetriesWithANewListenerAndServesHttp()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;

        var (listener, port) = TestLoopbackListener.Start(occupiedPort);
        using (listener)
        using (var client = new HttpClient())
        {
            Assert.NotEqual(occupiedPort, port);
            var response = client.GetStringAsync($"http://127.0.0.1:{port}/retry");
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("/retry", context.Request.Url!.AbsolutePath);
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync("ready"u8.ToArray());
            context.Response.Close();

            Assert.Equal("ready", await response.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }
}
