using System.Net;
using System.Net.Sockets;

namespace PiSharp.Tests;

internal static class TestLoopbackListener
{
    private static readonly object StartGate = new();

    public static (HttpListener Listener, int Port) Start(int? firstPort = null)
    {
        lock (StartGate)
        {
            HttpListenerException? lastError = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var port = firstPort is { } initial && attempt == 0 ? initial : ReservePort();

                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    return (listener, port);
                }
                catch (HttpListenerException error)
                {
                    try { listener.Close(); }
                    catch (HttpListenerException) { }
                    lastError = error;
                }
            }

            throw new InvalidOperationException("Could not start a loopback HTTP test listener after retrying port collisions.",
                lastError);
        }
    }

    private static int ReservePort()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }
}
