using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Tests;

public sealed class VirtualModelProcessTests
{
    [Fact]
    public async Task RpcRoutesUserToolContinuationAndNextTurnWithLogicalSelectionAndDurablePhysicalHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-virtual-process-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        var sessionPath = Path.Combine(root, "session.json");
        var routeLog = Path.Combine(root, "routes.jsonl");
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? process = null;
        var provider = Task.Run(async () =>
        {
            for (var index = 0; index < 3; index++)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                using var request = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: timeout.Token);
                Assert.Equal(index == 1 ? "large" : "small", request.RootElement.GetProperty("model").GetString());
                Assert.Equal("Bearer fixture-key", context.Request.Headers["Authorization"]);
                if (index == 1)
                    Assert.Contains(request.RootElement.GetProperty("messages").EnumerateArray(), message =>
                        message.GetProperty("role").GetString() == "tool");
                context.Response.ContentType = "text/event-stream";
                context.Response.SendChunked = true;
                await using var writer = new StreamWriter(context.Response.OutputStream);
                object delta = index == 0 ? new
                {
                    role = "assistant",
                    tool_calls = new[] { new { index = 0, id = "routed-call", type = "function",
                        function = new { name = "echo_ext", arguments = "{\"value\":\"routed\"}" } } }
                } : new { role = "assistant", content = "answer " + index };
                var chunk = JsonSerializer.Serialize(new
                {
                    id = "response-" + index,
                    @object = "chat.completion.chunk",
                    created = 1,
                    model = index == 1 ? "large" : "small",
                    choices = new[] { new { index = 0, delta, finish_reason = index == 0 ? "tool_calls" : "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 2, total_tokens = 12 }
                });
                await writer.WriteAsync("data: " + chunk + "\n\ndata: [DONE]\n\n");
                await writer.FlushAsync(timeout.Token);
                context.Response.Close();
            }
        }, timeout.Token);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new
                {
                    physical = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKey = "fixture-key",
                        api = "openai-completions",
                        models = new[] { new { id = "small", contextWindow = 32000 }, new { id = "large", contextWindow = 64000 } }
                    }
                }
            }));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var argument in new[] { "--mode", "rpc", "--provider", "test-router", "--model", "auto", "--offline",
                "--session", sessionPath, "--no-extensions", "--extension", typeof(FixtureExtension).Assembly.Location,
                "--tools", "echo_ext" }) start.ArgumentList.Add(argument);
            foreach (var name in new[] { "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_AUTH_PATH",
                "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH" }) start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_VIRTUAL_FIXTURE_LOG"] = routeLog;
            process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            var records = new List<JsonElement>();
            async Task Send(object command)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
                await process.StandardInput.FlushAsync(timeout.Token);
            }
            async Task ReadUntil(Func<JsonElement, bool> predicate)
            {
                try
                {
                    while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                    {
                        using var document = JsonDocument.Parse(line);
                        var record = document.RootElement.Clone();
                        records.Add(record);
                        if (predicate(record)) return;
                    }
                    throw new InvalidDataException("RPC ended early: " + await stderr);
                }
                catch (OperationCanceledException error)
                {
                    throw new TimeoutException("RPC records: " + JsonSerializer.Serialize(records) +
                        "; provider failure: " + provider.Exception, error);
                }
            }
            await Send(new { type = "prompt", message = "tool please" });
            await ReadUntil(record => record.GetProperty("type").GetString() == "agent_end");
            await ReadUntil(record => record.GetProperty("type").GetString() == "agent_settled");
            JsonElement settledState;
            do
            {
                await Send(new { type = "get_state", id = "settled" });
                await ReadUntil(record => record.TryGetProperty("id", out var id) && id.GetString() == "settled");
                settledState = records.Last().GetProperty("data");
            } while (settledState.GetProperty("isStreaming").GetBoolean());
            await Send(new { type = "prompt", message = "next turn" });
            await ReadUntil(record => record.GetProperty("type").GetString() == "agent_end");
            await Send(new { type = "get_state", id = "state" });
            await ReadUntil(record => record.TryGetProperty("id", out var id) && id.GetString() == "state");
            Assert.Equal("auto", records.Last().GetProperty("data").GetProperty("model").GetProperty("id").GetString());
            Assert.Equal("test-router", records.Last().GetProperty("data").GetProperty("model").GetProperty("provider").GetString());
            await Send(new { type = "get_messages", id = "messages" });
            await ReadUntil(record => record.TryGetProperty("id", out var id) && id.GetString() == "messages");
            var assistants = records.Last().GetProperty("data").GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "assistant").ToArray();
            Assert.Equal(new[] { "small", "large", "small" }, assistants.Select(message => message.GetProperty("model").GetString()));
            Assert.All(assistants, message => Assert.Equal("physical", message.GetProperty("provider").GetString()));
            Assert.All(assistants, message => Assert.Equal("openai-completions", message.GetProperty("api").GetString()));
            Assert.Contains(records, record => record.GetProperty("type").GetString() == "message_update" &&
                record.TryGetProperty("assistantMessageEvent", out var item) && item.GetProperty("type").GetString() == "toolcall_delta");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            await provider;
            var routes = (await File.ReadAllLinesAsync(routeLog)).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Assert.Equal(new[] { "user", "continuation", "user" }, routes.Select(route => route.GetProperty("reason").GetString()));
            Assert.Equal(new[] { 0, 1, 2 }, routes.Select(route => route.GetProperty("state").GetInt32()));
            Assert.Equal("small", routes[1].GetProperty("previous").GetString());
            Assert.Equal("large", routes[2].GetProperty("previous").GetString());
            var session = PiSharp.Runtime.Sessions.ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath));
            Assert.Equal("auto", session.Model);
            Assert.Equal(3, session.ActiveVirtualModelState("test-router", "auto")!.Value.GetInt32());
            Assert.Equal(new[] { "small", "large", "small" }, session.ActiveUsage().Select(usage => usage.Model));
        }
        finally
        {
            timeout.Cancel();
            if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            process?.Dispose();
            listener.Stop();
            try { await provider; } catch when (timeout.IsCancellationRequested) { }
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class VirtualModelFixtureExtension : IPiSharpExtension
{
    public void Configure(ExtensionRegistration registration)
    {
        if (Environment.GetEnvironmentVariable("PISHARP_VIRTUAL_FIXTURE_LOG") is not { } log) return;
        registration.RegisterVirtualModel(new("test-router", "auto", "Auto", async (request, cancellationToken) =>
        {
            var state = request.State is { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : 0;
            await File.AppendAllTextAsync(log, JsonSerializer.Serialize(new
            { reason = request.Reason, state, previous = request.PreviousModel?.Id }) + "\n", cancellationToken);
            return new("physical", request.Reason == "continuation" ? "large" : "small", "off", JsonSerializer.SerializeToElement(state + 1));
        }));
    }
}
