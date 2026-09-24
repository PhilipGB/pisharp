using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ProviderOverflowLoopbackTests
{
    [Theory]
    [InlineData("openai-responses")]
    [InlineData("openai-completions")]
    public async Task RealSdkDoesNotReplayAfterTextAndMalformedStream(string api)
    {
        using var listener = new HttpListener();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var requests = 0;
        var server = Task.Run(async () =>
        {
            var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
            Interlocked.Increment(ref requests);
            Assert.Equal(api == "openai-responses" ? "/v1/responses" : "/v1/chat/completions", request.Request.Url?.AbsolutePath);
            using var reader = new StreamReader(request.Request.InputStream);
            await reader.ReadToEndAsync(deadline.Token);
            request.Response.ContentType = "text/event-stream";
            await using var writer = new StreamWriter(request.Response.OutputStream);
            if (api == "openai-responses")
            {
                await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_partial\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"partial\"}\n\n");
            }
            else
                await writer.WriteAsync("data: {\"id\":\"chatcmpl_partial\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"partial\"},\"finish_reason\":null}]}\n\n");
            await writer.WriteAsync("data: {malformed-json}\n\n");
            await writer.FlushAsync(deadline.Token);
            request.Response.Close();
        }, deadline.Token);

        var model = new ModelDescriptor("fixture-model", "fixture", null, "fixture", Provider: "fixture", Api: api);
        var profile = new ProviderProfile("fixture", "fixture", new Uri($"http://127.0.0.1:{port}/v1"), true, false,
            null, null, [model]);
        var selection = new ModelSelection(profile, model, "fixture-key", true, "fixture");
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture-model", null);
        var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection),
            new CodingTools(Path.GetTempPath()), noTools: true,
            retryPolicy: new ProviderRetryPolicy(maxRetries: 2)), conversation);
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("say something", deadline.Token)) events.Add(item);
        await server.WaitAsync(deadline.Token);
        Assert.Equal(1, requests);
        Assert.Single(events, item => item.Type == "model_request_started");
        Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "partial");
        Assert.Contains(events, item => item.Type == "turn_failed");
        Assert.DoesNotContain(events, item => item.Type is "model_retry_scheduled" or "model_context_overflow_recovery" or "turn_completed");
        var interrupted = Assert.Single(conversation.Tree.Entries, entry => entry.Type == "interrupted");
        Assert.Equal("partial", interrupted.Payload.GetProperty("partialText").GetString());
    }

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("openai-completions")]
    public async Task RealSdkRecoversPreContentHttpOverflowWithoutReplacingCanonicalHistory(string api)
    {
        using var listener = new HttpListener();
        var port = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 9) { }
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bodies = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                Assert.Equal(api == "openai-responses" ? "/v1/responses" : "/v1/chat/completions", request.Request.Url?.AbsolutePath);
                using var reader = new StreamReader(request.Request.InputStream);
                bodies.Add(await reader.ReadToEndAsync(deadline.Token));
                await using var writer = new StreamWriter(request.Response.OutputStream);
                if (i == 0)
                {
                    request.Response.StatusCode = 400;
                    request.Response.ContentType = "application/json";
                    await writer.WriteAsync("""
                        {"error":{"message":"Your input exceeds the context window of this model","type":"invalid_request_error","code":"context_length_exceeded"}}
                        """);
                }
                else if (i == 1)
                {
                    request.Response.ContentType = "application/json";
                    await writer.WriteAsync(api == "openai-responses" ? """
                        {"id":"resp_summary","object":"response","created_at":1,"model":"fixture-model","status":"completed","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Earlier answer summarized.","annotations":[]}]}],"usage":{"input_tokens":20,"output_tokens":5,"total_tokens":25}}
                        """ : """
                        {"id":"chatcmpl_summary","object":"chat.completion","created":1,"model":"fixture-model","choices":[{"index":0,"message":{"role":"assistant","content":"Earlier answer summarized."},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":5,"total_tokens":25}}
                        """);
                }
                else
                {
                    request.Response.ContentType = "text/event-stream";
                    if (api == "openai-responses")
                    {
                        await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_ok\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                        await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"done\"}\n\n");
                    }
                    else
                        await writer.WriteAsync("data: {\"id\":\"chatcmpl_ok\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"done\"},\"finish_reason\":null}]}\n\n");
                    await writer.WriteAsync("data: [DONE]\n\n");
                }
                await writer.FlushAsync(deadline.Token);
                request.Response.Close();
            }
        }, deadline.Token);

        var model = new ModelDescriptor("fixture-model", "fixture", null, "fixture", Provider: "fixture", Api: api);
        var profile = new ProviderProfile("fixture", "fixture", new Uri($"http://127.0.0.1:{port}/v1"), true, false,
            null, null, [model]);
        var selection = new ModelSelection(profile, model, "fixture-key", true, "fixture");
        var conversation = new ConversationSession(Path.GetTempPath(), "fixture-model", null);
        conversation.Append(new ChatMessage(ChatRole.User, new string('P', 900)));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "previous answer"));
        var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection),
            new CodingTools(Path.GetTempPath()), noTools: true), conversation,
            autoCompaction: new AutoCompactionPolicy(4000, 500));
        var events = new List<AgentLifecycleEvent>();
        await foreach (var item in run.RunEventsAsync("new prompt", deadline.Token)) events.Add(item);
        await server.WaitAsync(deadline.Token);
        Assert.Contains(events, item => item.Type == "model_context_overflow_recovery");
        Assert.Contains(events, item => item.Type == "turn_completed");
        Assert.Equal(3, bodies.Count);
        Assert.Contains(new string('P', 900), bodies[0]);
        Assert.Contains("Earlier answer summarized.", bodies[2]);
        Assert.DoesNotContain(new string('P', 900), bodies[2]);
        Assert.Equal(900, conversation.ActiveMessages()[0].Text.Length);
        Assert.Contains(conversation.ActiveMessages(), message => message.Text == "done");
    }
}
