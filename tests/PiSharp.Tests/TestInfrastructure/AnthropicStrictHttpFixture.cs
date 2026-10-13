using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

internal sealed class AnthropicStrictHttpFixture : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
    private readonly Task _server;
    private readonly Uri _endpoint;
    private int _closed;
    private bool _disposed;
    internal List<JsonElement> Payloads { get; } = [];
    internal CancellationToken Token => _deadline.Token;
    internal Uri Endpoint => _endpoint;

    internal AnthropicStrictHttpFixture(Func<JsonElement, int, (int Status, string Body)> respond, bool ignoreDisconnect = false)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        _endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/");
        reservation.Stop();
        _listener.Prefixes.Add(_endpoint.AbsoluteUri);
        _listener.IgnoreWriteExceptions = ignoreDisconnect;
        _listener.Start();
        _server = Task.Run(async () =>
        {
            try
            {
                while (!_deadline.IsCancellationRequested)
                {
                    var request = await _listener.GetContextAsync().WaitAsync(Token);
                    using var body = await JsonDocument.ParseAsync(request.Request.InputStream, cancellationToken: Token);
                    var payload = body.RootElement.Clone();
                    Payloads.Add(payload);
                    var response = respond(payload, Payloads.Count);
                    request.Response.StatusCode = response.Status;
                    var streaming = response.Status == 200 && payload.TryGetProperty("stream", out var stream) && stream.GetBoolean();
                    request.Response.ContentType = streaming ? "text/event-stream" : "application/json";
                    await using var writer = new StreamWriter(request.Response.OutputStream);
                    await writer.WriteAsync(streaming && response.Body.StartsWith("event:", StringComparison.Ordinal) ? response.Body : streaming ? """
                        event: message_start
                        data: {"type":"message_start","message":{"id":"msg_fixture","type":"message","role":"assistant","model":"fixture","content":[],"usage":{"input_tokens":4,"output_tokens":0}}}

                        event: content_block_start
                        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                        event: content_block_delta
                        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

                        event: content_block_stop
                        data: {"type":"content_block_stop","index":0}

                        event: message_delta
                        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}

                        event: message_stop
                        data: {"type":"message_stop"}


                        """ : response.Body);
                    await writer.FlushAsync(Token);
                    request.Response.Close();
                }
            }
            catch (Exception error) when (_deadline.IsCancellationRequested &&
                error is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            { }
        });
    }

    internal IChatClient Client(bool nativeChanges = false, bool strictSupport = true)
    {
        var model = new ModelDescriptor("fixture", "fixture", 128000, "configured", Provider: "fixture",
            Api: "anthropic-messages", Compatibility: JsonSerializer.SerializeToElement(new
            {
                supportsStrictTools = strictSupport,
                supportsMidConvoSystemMessages = nativeChanges,
                supportsMidConvoToolChanges = nativeChanges
            }));
        var profile = new ProviderProfile("fixture", "Fixture", _endpoint, true, false, null, null, [model], Api: "anthropic-messages");
        return ProviderChatClientFactory.Create(new ModelSelection(profile, model, "fixture-key", true, "fixture"),
            new ProviderRetrySettings { MaxRetries = 0 });
    }

    internal async Task StopAsync()
    {
        _deadline.Cancel();
        CloseListener();
        await _server;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deadline.Cancel();
        CloseListener();
        _deadline.Dispose();
    }

    private void CloseListener()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) _listener.Close();
    }
}
