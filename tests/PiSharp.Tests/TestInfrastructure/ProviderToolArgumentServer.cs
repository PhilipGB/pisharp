using System.Net;
using System.Text.Json;

namespace PiSharp.Tests;

internal sealed class ProviderToolArgumentServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));
    private readonly Task _serve;

    public ProviderToolArgumentServer(string protocol, string arguments, bool streaming, int requests = 1)
    {
        var (listener, port) = TestLoopbackListener.Start();
        _listener = listener;
        Endpoint = new Uri("http://127.0.0.1:" + port);
        _serve = ServeAsync(protocol, arguments, streaming, requests);
    }

    public Uri Endpoint { get; }
    public JsonElement LastRequest { get; private set; }

    private async Task ServeAsync(string protocol, string arguments, bool streaming, int requests)
    {
        try
        {
            for (var index = 0; index < requests; index++)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                using var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: _stop.Token);
                LastRequest = body.RootElement.Clone();
                context.Response.ContentType = streaming ? "text/event-stream" : "application/json";
                var payload = Response(protocol, arguments, streaming);
                await using var writer = new StreamWriter(context.Response.OutputStream);
                await writer.WriteAsync(payload.AsMemory(), _stop.Token);
                await writer.FlushAsync(_stop.Token);
                context.Response.Close();
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or HttpListenerException) { }
    }

    internal static string Response(string protocol, string arguments, bool streaming)
    {
        var split = arguments.Length / 2;
        var fragments = new[] { arguments[..split], arguments[split..] };
        if (protocol == "openai-completions")
        {
            object Call(string fragment, bool first) => new
            {
                index = 0,
                id = first ? "call_fixture" : null,
                type = "function",
                function = new { name = first ? "fixture_tool" : null, arguments = fragment }
            };
            if (!streaming)
                return JsonSerializer.Serialize(new
                {
                    id = "chat_fixture",
                    @object = "chat.completion",
                    created = 1,
                    model = "fixture-model",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = (string?)null,
                        tool_calls = new[] { Call(arguments, true) } }, finish_reason = "tool_calls" } }
                });
            return string.Concat(fragments.Select((fragment, index) => "data: " + JsonSerializer.Serialize(new
            {
                id = "chat_fixture",
                @object = "chat.completion.chunk",
                created = 1,
                model = "fixture-model",
                choices = new[] { new { index = 0, delta = new { role = "assistant", tool_calls = new[] { Call(fragment, index == 0) } },
                    finish_reason = index == 1 ? "tool_calls" : null } }
            }) + "\n\n")) + "data: [DONE]\n\n";
        }
        if (protocol == "anthropic-messages")
        {
            object message = new
            {
                id = "msg_fixture",
                type = "message",
                role = "assistant",
                model = "fixture-model",
                content = Array.Empty<object>(),
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = 2, output_tokens = 0 }
            };
            object[] events =
            [
                new { type = "message_start", message },
                new { type = "content_block_start", index = 0, content_block = new
                    { type = "tool_use", id = "call_fixture", name = "fixture_tool", input = new { } } },
                .. fragments.Select(fragment => (object)new { type = "content_block_delta", index = 0,
                    delta = new { type = "input_json_delta", partial_json = fragment } }),
                new { type = "content_block_stop", index = 0 },
                new { type = "message_delta", delta = new { stop_reason = "tool_use", stop_sequence = (string?)null },
                    usage = new { output_tokens = 5 } },
                new { type = "message_stop" }
            ];
            return string.Concat(events.Select(Event));
        }
        object tool = new
        {
            type = "function_call",
            id = "fc_fixture",
            call_id = "call_fixture",
            name = "fixture_tool",
            arguments,
            status = "completed"
        };
        object response = new
        {
            id = "resp_fixture",
            @object = "response",
            created_at = 1,
            model = "fixture-model",
            status = "completed",
            output = new[] { tool },
            usage = new { input_tokens = 2, output_tokens = 5, total_tokens = 7 }
        };
        if (!streaming) return JsonSerializer.Serialize(response);
        object[] responseEvents =
        [
            new { type = "response.created", response, sequence_number = 0 },
            new { type = "response.output_item.added", output_index = 0, item = tool, sequence_number = 1 },
            new { type = "response.output_item.done", output_index = 0, item = tool, sequence_number = 2 },
            new { type = "response.completed", response, sequence_number = 3 }
        ];
        return string.Concat(responseEvents.Select(Event));
    }

    private static string Event(object value) => "event: " + JsonSerializer.SerializeToElement(value)
        .GetProperty("type").GetString() + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        await _serve;
        _stop.Dispose();
    }
}
