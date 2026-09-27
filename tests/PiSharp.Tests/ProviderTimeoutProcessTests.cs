using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ProviderTimeoutProcessTests
{
    [Fact]
    public async Task SavedProviderTimeoutStopsARealCliRequest()
    {
        using var listener = StartLoopbackListener(out var port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var stopServer = new CancellationTokenSource();
        var root = Path.Combine(Path.GetTempPath(), "pisharp-provider-timeout-process-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"),
                "{\"httpIdleTimeoutMs\":5000,\"retry\":{\"enabled\":false,\"provider\":{\"maxRetries\":0,\"timeoutMs\":1000}}}");
            var modelCatalog = "{\"providers\":{\"fixture\":{\"baseUrl\":\"http://127.0.0.1:" + port +
                "/v1\",\"apiKeyEnv\":\"PISHARP_FIXTURE_KEY\",\"models\":[{\"id\":\"fixture-model\",\"api\":\"openai-responses\"}]}}}";
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), modelCatalog);

            var requests = 0;
            var responses = new List<Task>();
            var server = Task.Run(async () =>
            {
                while (!stopServer.IsCancellationRequested)
                {
                    HttpListenerContext request;
                    try { request = await listener.GetContextAsync().WaitAsync(stopServer.Token); }
                    catch (OperationCanceledException) when (stopServer.IsCancellationRequested) { break; }
                    catch (HttpListenerException) when (stopServer.IsCancellationRequested) { break; }
                    Interlocked.Increment(ref requests);
                    using var reader = new StreamReader(request.Request.InputStream);
                    await reader.ReadToEndAsync(deadline.Token);
                    responses.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(2_500), deadline.Token);
                            request.Response.ContentType = "text/event-stream";
                            await using var writer = new StreamWriter(request.Response.OutputStream);
                            await writer.WriteAsync("data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_late\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"in_progress\",\"output\":[]}}\n\n");
                            await writer.WriteAsync("data: {\"type\":\"response.output_text.delta\",\"item_id\":\"item_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"late\"}\n\n");
                            await writer.WriteAsync("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_late\",\"object\":\"response\",\"created_at\":1,\"model\":\"fixture-model\",\"status\":\"completed\",\"output\":[]}}\n\ndata: [DONE]\n\n");
                            await writer.FlushAsync();
                        }
                        catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
                        finally { request.Response.Close(); }
                    }));
                }
            });

            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "fixture", "--model",
                "fixture-model", "--print", "hello", "--no-tools", "--no-session", "--offline" })
                start.ArgumentList.Add(argument);
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "timeout-fixture-key";
            foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                start.Environment.Remove(name);
            start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            start.Environment["no_proxy"] = "127.0.0.1,localhost";

            using var process = Process.Start(start);
            Assert.NotNull(process);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(deadline.Token);
            stopServer.Cancel();
            listener.Close();
            await server.WaitAsync(deadline.Token);
            await Task.WhenAll(responses).WaitAsync(deadline.Token);

            Assert.Equal(1, process.ExitCode);
            Assert.DoesNotContain("late", await stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("timed out", await stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, requests);
        }
        finally
        {
            stopServer.Cancel();
            listener.Close();
            Directory.Delete(root, recursive: true);
        }
    }

    private static HttpListener StartLoopbackListener(out int port)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var candidate = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
                if (attempt == 9) throw;
            }
        }
        throw new InvalidOperationException("Could not reserve a loopback port for the timeout fixture.");
    }
}
