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

    [Fact]
    public async Task RealCompletionsSdkDoesNotReplayCompletedWriteAfterPartialContinuationFails()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-post-tool-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var bodies = new List<string>();
            var server = Task.Run(async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var request = await listener.GetContextAsync().WaitAsync(deadline.Token);
                    Assert.Equal("/v1/chat/completions", request.Request.Url?.AbsolutePath);
                    using var reader = new StreamReader(request.Request.InputStream);
                    bodies.Add(await reader.ReadToEndAsync(deadline.Token));
                    request.Response.ContentType = "text/event-stream";
                    await using var writer = new StreamWriter(request.Response.OutputStream);
                    if (i == 0)
                    {
                        await writer.WriteAsync("data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"write\",\"arguments\":\"{\\\"path\\\":\\\"result.txt\\\",\\\"content\\\":\\\"made\\\"}\"}}]},\"finish_reason\":null}]}\n\n");
                        await writer.WriteAsync("data: {\"id\":\"chatcmpl_tool\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
                        await writer.WriteAsync("data: [DONE]\n\n");
                    }
                    else if (i == 1)
                    {
                        await writer.WriteAsync("data: {\"id\":\"chatcmpl_partial\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"partial\"},\"finish_reason\":null}]}\n\n");
                        await writer.WriteAsync("data: {malformed-json}\n\n");
                    }
                    else
                    {
                        await writer.WriteAsync("data: {\"id\":\"chatcmpl_recovered\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"recovered\"},\"finish_reason\":null}]}\n\n");
                        await writer.WriteAsync("data: {\"id\":\"chatcmpl_recovered\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                        await writer.WriteAsync("data: [DONE]\n\n");
                    }
                    await writer.FlushAsync(deadline.Token);
                    request.Response.Close();
                }
            }, deadline.Token);

            var model = new ModelDescriptor("fixture-model", "fixture", null, "fixture", Provider: "fixture", Api: "openai-completions");
            var profile = new ProviderProfile("fixture", "fixture", new Uri($"http://127.0.0.1:{port}/v1"), true, false,
                null, null, [model]);
            var selection = new ModelSelection(profile, model, "fixture-key", true, "fixture");
            var conversation = new ConversationSession(cwd, "fixture-model", null);
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var path = store.NewPath(conversation);
            var run = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection),
                new CodingTools(cwd), retryPolicy: new ProviderRetryPolicy(maxRetries: 2)), conversation,
                save: token => store.SaveAsync(conversation, path, token));
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("write the file", deadline.Token)) events.Add(item);
            Assert.Equal(2, bodies.Count);
            Assert.Contains("call-1", bodies[1]);
            Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(cwd, "result.txt"), deadline.Token));
            Assert.Single(events, item => item.Type == "tool_execution_started");
            Assert.Equal(2, events.Count(item => item.Type == "model_request_started"));
            Assert.Contains(events, item => item.Type == "model_text_delta" && item.Text == "partial");
            Assert.Contains(events, item => item.Type == "turn_failed");
            Assert.DoesNotContain(events, item => item.Type is "model_retry_scheduled" or "turn_completed");
            var persisted = await store.LoadAsync(path, deadline.Token);
            Assert.Single(persisted.Tree.Entries, entry => entry.Type == "tool_intent");
            Assert.Single(persisted.Tree.Entries, entry => entry.Type == "tool_outcome");
            Assert.Contains(persisted.Tree.Entries, entry => entry.Type == "run_finished" &&
                !entry.Payload.GetProperty("completed").GetBoolean());
            var interrupted = Assert.Single(persisted.Tree.Entries, entry => entry.Type == "interrupted");
            Assert.Equal("partial", interrupted.Payload.GetProperty("partialText").GetString());
            Assert.True(persisted.RecoverIncomplete());
            Assert.False(persisted.RecoverIncomplete());
            Assert.Contains("No tool outcome is unknown", persisted.ActiveMessages().Last().Text);
            var resumed = await ConversationRun.OpenAsync(new PiAgent(ProviderChatClientFactory.Create(selection),
                new CodingTools(cwd), retryPolicy: new ProviderRetryPolicy(maxRetries: 2)), persisted,
                save: token => store.SaveAsync(persisted, path, token));
            var continuation = new List<AgentLifecycleEvent>();
            await foreach (var item in resumed.RunEventsAsync("continue without rewriting", deadline.Token)) continuation.Add(item);
            await server.WaitAsync(deadline.Token);
            Assert.Equal(3, bodies.Count);
            Assert.Contains("No tool outcome is unknown", bodies[2]);
            Assert.Contains(continuation, item => item.Type == "turn_completed");
            Assert.DoesNotContain(continuation, item => item.Type == "tool_execution_started");
            Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(cwd, "result.txt"), deadline.Token));
            Assert.Single(persisted.Tree.Entries, entry => entry.Type == "tool_intent");
            Assert.Single(persisted.Tree.Entries, entry => entry.Type == "tool_outcome");
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Theory]
    [InlineData("openai-responses")]
    [InlineData("openai-completions")]
    public async Task RealSdkRecoversPreContentHttpOverflowWithoutReplacingCanonicalHistory(string api)
    {
        using var listener = StartLoopbackListener(out var port);
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

    private static HttpListener StartLoopbackListener(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var candidatePort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");
            try
            {
                listener.Start();
                port = candidatePort;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
                if (attempt == 9) throw;
            }
        }

        throw new InvalidOperationException("Could not reserve a loopback port for the provider fixture.");
    }
}
