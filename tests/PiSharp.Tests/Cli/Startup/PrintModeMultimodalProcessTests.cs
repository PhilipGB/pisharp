using System.Diagnostics;
using System.Net;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class PrintModeMultimodalProcessTests
{
    [Fact]
    public async Task PositionalImageIsSentWithPrintModePrompt()
    {
        var (listener, port) = TestLoopbackListener.Start();
        using var server = listener;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = Path.Combine(Path.GetTempPath(), "pisharp-print-image-" + Guid.NewGuid().ToString("N"));
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
                        models = new[] { new { id = "fixture-model", api = "openai-completions", input = new[] { "text", "image" } } }
                    }
                }
            }));
            await File.WriteAllBytesAsync(Path.Combine(root, "diagram.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="));

            JsonDocument? requestBody = null;
            var requestArrived = false;
            var serve = Task.Run(async () =>
            {
                var request = await server.GetContextAsync().WaitAsync(deadline.Token);
                requestArrived = true;
                using var reader = new StreamReader(request.Request.InputStream);
                requestBody = JsonDocument.Parse(await reader.ReadToEndAsync(deadline.Token));
                request.Response.ContentType = "text/event-stream";
                await using var writer = new StreamWriter(request.Response.OutputStream);
                var chunks = new object[]
                {
                    new
                    {
                        id = "print-image-fixture", @object = "chat.completion.chunk", created = 1,
                        model = "fixture-model",
                        choices = new[] { new { index = 0, delta = new { role = "assistant", content = "seen" }, finish_reason = (string?)null } }
                    },
                    new
                    {
                        id = "print-image-fixture", @object = "chat.completion.chunk", created = 1,
                        model = "fixture-model",
                        choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }
                    }
                };
                foreach (var chunk in chunks)
                    await writer.WriteAsync("data: " + JsonSerializer.Serialize(chunk) + "\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(deadline.Token);
            }, deadline.Token);

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "fixture",
                "--model", "fixture-model", "--print", "--no-tools", "--no-session", "--offline",
                "describe this", "@diagram.png" })
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
                var output = await stdout;
                var error = await stderr;
                Assert.True(requestArrived, $"The CLI made no provider request. Exit code: {process.ExitCode}. stdout: {output} stderr: {error}");
                await serve;
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("seen" + Environment.NewLine, output);
                Assert.Equal(string.Empty, error);

                var userMessage = requestBody!.RootElement.GetProperty("messages").EnumerateArray()
                    .Single(message => message.GetProperty("role").GetString() == "user");
                var content = userMessage.GetProperty("content");
                Assert.Equal(JsonValueKind.Array, content.ValueKind);
                Assert.Contains(content.EnumerateArray(), item => item.GetProperty("type").GetString() == "text" &&
                    item.GetProperty("text").GetString()!.Contains("describe this", StringComparison.Ordinal));
                var image = Assert.Single(content.EnumerateArray(), item => item.GetProperty("type").GetString() == "image_url");
                Assert.StartsWith("data:image/png;base64,", image.GetProperty("image_url").GetProperty("url").GetString());
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
