using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class HttpProxyProcessTests
{
    [Fact]
    public async Task UserHttpProxyRoutesAProviderRequestThroughTheConfiguredProxy()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-http-proxy-process-" + Guid.NewGuid().ToString("N"));
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(agentDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "settings.json"),
                $$"""{"httpProxy":"http://127.0.0.1:{{proxyPort}}"}""");
            await File.WriteAllTextAsync(Path.Combine(agentDirectory, "models.json"), """
                {"providers":{"fixture":{"baseUrl":"http://model.invalid/v1","apiKeyEnv":"PISHARP_FIXTURE_KEY","models":[{"id":"fixture-model","api":"openai-responses"}]}}}
                """);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var proxy = AcceptAndRespondAsync(listener, timeout.Token);
            var cliAssembly = typeof(CliArguments).Assembly.Location;
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(cliAssembly);
            start.ArgumentList.Add("--provider");
            start.ArgumentList.Add("fixture");
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add("fixture-model");
            start.ArgumentList.Add("--print");
            start.ArgumentList.Add("hello");
            start.ArgumentList.Add("--no-tools");
            start.ArgumentList.Add("--no-session");
            start.ArgumentList.Add("--offline");
            start.Environment["PISHARP_AGENT_DIR"] = agentDirectory;
            start.Environment["PISHARP_FIXTURE_KEY"] = "proxy-fixture-key";
            start.Environment.Remove("HTTP_PROXY");
            start.Environment.Remove("HTTPS_PROXY");
            start.Environment.Remove("ALL_PROXY");
            start.Environment.Remove("http_proxy");
            start.Environment.Remove("https_proxy");
            start.Environment.Remove("all_proxy");
            start.Environment["NO_PROXY"] = "";
            start.Environment["no_proxy"] = "";

            using var process = Process.Start(start);
            Assert.NotNull(process);
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                var timedOutOutput = await stdout;
                var timedOutError = await stderr;
                throw new InvalidOperationException(
                    $"CLI did not exit through the configured proxy. Proxy task state: {proxy.Status}. stdout: {timedOutOutput}; stderr: {timedOutError}");
            }
            var output = await stdout;
            var error = await stderr;
            var requestLine = await proxy;

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("proxy-ok", output);
            Assert.DoesNotContain("Agent error:", error);
            Assert.Contains("http://model.invalid/v1/responses", requestLine);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<string> AcceptAndRespondAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        var headerBytes = new List<byte>();
        var matched = 0;
        var delimiter = new byte[] { 13, 10, 13, 10 };
        var oneByte = new byte[1];
        while (matched < delimiter.Length)
        {
            var read = await stream.ReadAsync(oneByte, cancellationToken);
            if (read == 0) throw new EndOfStreamException("Provider proxy request ended before its headers completed.");
            var value = oneByte[0];
            headerBytes.Add(value);
            matched = value == delimiter[matched] ? matched + 1 : value == delimiter[0] ? 1 : 0;
            if (headerBytes.Count > 64 * 1024) throw new InvalidDataException("Provider proxy request headers exceeded 64KB.");
        }

        var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
        var responseBody = Encoding.UTF8.GetBytes("""
            data: {"type":"response.created","response":{"id":"resp_proxy","object":"response","created_at":1,"model":"fixture-model","status":"in_progress","output":[]}}

            data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"proxy-ok"}

            data: {"type":"response.completed","response":{"id":"resp_proxy","object":"response","created_at":1,"model":"fixture-model","status":"completed","output":[]}}

            data: [DONE]

            """);
        var responseHeader = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {responseBody.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeader, cancellationToken);
        await stream.WriteAsync(responseBody, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return headers.Split("\r\n", StringSplitOptions.None)[0];
    }
}
