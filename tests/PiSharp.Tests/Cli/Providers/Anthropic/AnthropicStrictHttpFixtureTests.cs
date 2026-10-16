using System.Net;
using System.Net.Sockets;

namespace PiSharp.Tests;

public sealed class AnthropicStrictHttpFixtureTests
{
    [Fact]
    public async Task DisposingAStoppedFixtureDoesNotReacquireItsReusedPort()
    {
        using var fixture = new AnthropicStrictHttpFixture((_, _) => throw new InvalidOperationException("Unexpected request."));
        var port = fixture.Endpoint.Port;
        await fixture.StopAsync();
        using var nextOwner = new TcpListener(IPAddress.Loopback, port);
        nextOwner.Start();
        fixture.Dispose();
        fixture.Dispose();
        Assert.Equal(port, ((IPEndPoint)nextOwner.LocalEndpoint).Port);
    }
}
