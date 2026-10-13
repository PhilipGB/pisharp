using System.Diagnostics;
using System.Net;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class PrintModeTruncationProcessTests
{
    [Theory]
    [InlineData("print", "length", 1)]
    [InlineData("print", "stop", 0)]
    [InlineData("json", "length", 0)]
    [InlineData("json", "stop", 0)]
    public async Task OutputLimitKeepsPartialTextAndOnlyTextModeWarns(string mode, string finishReason, int exitCode)
    {
        var (listener, port) = TestLoopbackListener.Start();
        using var server = listener;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = Path.Combine(Path.GetTempPath(), "pisharp-print-length-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"),
                "{\"compaction\":{\"enabled\":false},\"retry\":{\"enabled\":false}}");
            await File.WriteAllTextAsync(Path.Combine(root, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new Dictionary<string, object>
                {
                    ["fixture"] = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKeyEnv = "PISHARP_FIXTURE_KEY",
                        models = new[] { new { id = "fixture-model", api = "openai-completions" } }
                    }
                }
            }));
            var serve = Task.Run(async () =>
            {
                var request = await server.GetContextAsync().WaitAsync(deadline.Token);
                using var reader = new StreamReader(request.Request.InputStream);
                using var body = JsonDocument.Parse(await reader.ReadToEndAsync(deadline.Token));
                Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
                request.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                foreach (var (delta, reason) in new (object, string?)[]
                {
                    (new { role = "assistant", content = "partial response" }, null),
                    (new { }, finishReason)
                })
                {
                    var chunk = new
                    {
                        id = "length-fixture",
                        @object = "chat.completion.chunk",
                        created = 1,
                        model = "fixture-model",
                        choices = new[] { new { index = 0, delta, finish_reason = reason } }
                    };
                    await writer.WriteAsync("data: " + JsonSerializer.Serialize(chunk) + "\n\n");
                }
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(deadline.Token);
            }, deadline.Token);
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "fixture",
                "--model", "fixture-model", "--mode", mode, "hello", "--no-tools", "--no-session", "--offline" })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_AGENT_DIR"] = root;
            start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(root, "sessions");
            start.Environment["PISHARP_FIXTURE_KEY"] = "fixture-key";
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                start.Environment.Remove(name);
            start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            start.Environment["no_proxy"] = "127.0.0.1,localhost";
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                await serve;
                Assert.Equal(exitCode, process.ExitCode);
                Assert.Contains("partial response", await stdout);
                if (mode == "print") Assert.Equal("partial response" + Environment.NewLine, await stdout);
                Assert.Equal(mode == "print" && finishReason == "length"
                    ? "Response was truncated: the model reached its output token limit." + Environment.NewLine
                    : "", await stderr);
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
