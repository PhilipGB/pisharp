using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CrashRecoveryProcessTests
{
    [Fact]
    public async Task KilledCliKeepsToolCheckpointAndNeverClaimsTurnCompleted()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sessionPath = Path.Combine(root, "record.session.json");
        var server = Task.Run(async () =>
        {
            var first = await listener.GetContextAsync().WaitAsync(timeout.Token);
            first.Response.ContentType = "text/event-stream";
            await using (var writer = new StreamWriter(first.Response.OutputStream))
            {
                var chunk = new
                {
                    id = "chatcmpl-1",
                    @object = "chat.completion.chunk",
                    created = 1,
                    model = "fixture",
                    choices = new[] { new { index = 0,
                        delta = new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call-1", type = "function",
                            function = new { name = "write", arguments = "{\"path\":\"result.txt\",\"content\":\"made\"}" } } } },
                        finish_reason = (string?)null } }
                };
                await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                await writer.WriteAsync("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync(timeout.Token);
            }
            first.Response.Close();
            var second = await listener.GetContextAsync().WaitAsync(timeout.Token);
            // Keep the model request in-flight until SIGKILL; the assistant never completed.
            await Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
            second.Response.Close();
        });
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(CliArguments).Assembly.Location);
            start.ArgumentList.Add("--session"); start.ArgumentList.Add(sessionPath);
            start.ArgumentList.Add("--print"); start.ArgumentList.Add("Create result.txt");
            start.Environment["PISHARP_BASE_URL"] = $"http://127.0.0.1:{port}/v1";
            start.Environment["PISHARP_MODEL"] = "fixture";
            start.Environment["PISHARP_API_KEY"] = "not-needed";
            start.Environment["PISHARP_AGENT_DIR"] = Path.Combine(root, "empty-agent");
            start.Environment.Remove("OPENAI_API_KEY");
            using var process = Process.Start(start);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    if (File.Exists(sessionPath))
                    {
                        try
                        {
                            var snapshot = ConversationSession.Parse(await File.ReadAllTextAsync(sessionPath, timeout.Token));
                            if (snapshot.Tree.ActivePath().Any(node => node.Type == "tool_outcome")) break;
                        }
                        catch (IOException) { }
                    }
                    if (process.HasExited) throw new InvalidOperationException("CLI exited before tool checkpoint: " + await error);
                    await Task.Delay(25, timeout.Token);
                }
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(timeout.Token);
                _ = await output; _ = await error;
                Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(root, "result.txt"), timeout.Token));
                var store = new ConversationStore(root, root);
                var recovered = await store.LoadAsync(sessionPath, timeout.Token);
                Assert.DoesNotContain(recovered.Tree.ActivePath(), node => node.Type == "run_finished");
                Assert.True(recovered.RecoverIncomplete());
                Assert.Contains("No tool outcome is unknown", recovered.ActiveMessages().Last().Text);
                Assert.Equal("made", await File.ReadAllTextAsync(Path.Combine(root, "result.txt"), timeout.Token));
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await server; } catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            Directory.Delete(root, recursive: true);
        }
    }
}
