using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

[Collection("Editor mutation PTY")]
public sealed class BranchSummaryReserveTokensPtyTests
{
    [Theory]
    [InlineData("80", 100, false, false)]
    [InlineData("100", 100, true, false)]
    [InlineData("\"bad\"", 100, true, false)]
    [InlineData("-1", 100, true, false)]
    [InlineData("2.5", 32, false, false)]
    [InlineData("\"bad\"", 100, false, true)]
    public async Task TreeSummarizeChoiceUsesFileReserveInPhysicalProviderRequest(string reserve, int context,
        bool includesOldest, bool skipPrompt)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-reserve-pty-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        var (listener, port) = TestLoopbackListener.Start();
        using var stop = new CancellationTokenSource();
        var requests = new List<string>();
        Process? process = null;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var request = await listener.GetContextAsync().WaitAsync(stop.Token);
                    using var reader = new StreamReader(request.Request.InputStream);
                    requests.Add(await reader.ReadToEndAsync(stop.Token));
                    request.Response.ContentType = "text/event-stream";
                    await using var writer = new StreamWriter(request.Response.OutputStream);
                    await writer.WriteAsync("data: {\"id\":\"summary\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"fixture summary\"},\"finish_reason\":null}]}\n\n");
                    await writer.WriteAsync("data: {\"id\":\"summary\",\"object\":\"chat.completion.chunk\",\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                }
            }
            catch (Exception error) when (error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
        });
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
                "{\"quietStartup\":true,\"tuiMode\":\"fullscreen\",\"branchSummary\":{\"skipPrompt\":" +
                (skipPrompt ? "true" : "false") + ",\"reserveTokens\":" + reserve + "}}");
            await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new
                {
                    fixture = new
                    {
                        baseUrl = $"http://127.0.0.1:{port}/v1",
                        apiKey = "fixture-key",
                        models = new[] { new { id = "fixture-model", api = "openai-completions", contextWindow = context, maxTokens = 8192 } }
                    }
                }
            }));
            var store = new ConversationStore(root, Path.Combine(root, "sessions"));
            var conversation = new ConversationSession(root, "fixture-model", $"http://127.0.0.1:{port}/v1", provider: "fixture");
            conversation.Append(new ChatMessage(ChatRole.User, "SHARED"));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "TARGET"));
            var target = conversation.Tree.HeadId;
            conversation.Append(new ChatMessage(ChatRole.User, "OLDEST-MARKER".PadRight(40, 'x')));
            conversation.Append(new ChatMessage(ChatRole.Assistant, "MIDDLE-MARKER".PadRight(40, 'x')));
            conversation.Append(new ChatMessage(ChatRole.User, "RECENT-MARKER".PadRight(40, 'x')));
            var path = store.NewPath(conversation);
            await store.SaveAsync(conversation, path);
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-q", "-e", "-c", $"stty rows 30 cols 100; exec dotnet '{typeof(CliArguments).Assembly.Location}' --offline --session '{path}' --session-dir '{store.DirectoryPath}' --no-tools", "/dev/null" }
            };
            start.Environment["PISHARP_AGENT_DIR"] = agent;
            process = Process.Start(start)!;
            var screen = new PtyScreenCapture(process, rows: 30, columns: 100);
            var stdout = screen.ReadCompletion;
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await screen.InputReady.WaitAsync(timeout.Token);
            async Task SendAsync(string text)
            {
                await process.StandardInput.WriteAsync(text);
                await process.StandardInput.FlushAsync();
            }
            async Task WaitAsync(string text)
            {
                try { await screen.WaitForFrameAsync(frame => frame.Contains(text, StringComparison.Ordinal)).WaitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"Waiting for {text}:\n{screen.LastFrame}");
                }
            }
            await SendAsync("/tree\r");
            await WaitAsync("Session Tree");
            await SendAsync("target");
            await WaitAsync("Type to search: target");
            await SendAsync("\r");
            if (!skipPrompt)
            {
                await WaitAsync("Summarize branch?");
                await SendAsync("\u001b[B\r");
                await WaitAsync("fixture summary");
            }
            else await screen.WaitForFrameAsync(frame => !frame.Contains("Session Tree", StringComparison.Ordinal) &&
                frame.Contains("TARGET", StringComparison.Ordinal)).WaitAsync(timeout.Token);
            await SendAsync("\u0015/quit\r");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            _ = await stdout;
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("Agent error", await stderr);
            var saved = await store.LoadAsync(path);
            if (skipPrompt)
            {
                Assert.Empty(requests);
                Assert.DoesNotContain(saved.Tree.Entries, node => node.Type == "branch_summary");
                Assert.Equal(target, saved.Tree.HeadId);
            }
            else
            {
                using var request = JsonDocument.Parse(Assert.Single(requests));
                var text = string.Join("\n", request.RootElement.GetProperty("messages").EnumerateArray()
                    .Select(message => message.GetProperty("content").ToString()));
                Assert.Equal(includesOldest, text.Contains("OLDEST-MARKER", StringComparison.Ordinal));
                Assert.Contains("MIDDLE-MARKER", text);
                Assert.Contains("RECENT-MARKER", text);
                var cap = request.RootElement.TryGetProperty("max_completion_tokens", out var completionCap)
                    ? completionCap : request.RootElement.GetProperty("max_tokens");
                Assert.Equal(4096, cap.GetInt32());
                Assert.Equal(target, Assert.Single(saved.Tree.Entries, node => node.Type == "branch_summary").ParentId);
            }
            if (Environment.GetEnvironmentVariable("PISHARP_BRANCH_RESERVE_PTY_OUTPUT_DIR") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                var name = $"{reserve.Replace('"', '_')}-{context}-{skipPrompt}.json";
                await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(new
                {
                    reserve,
                    contextWindow = context,
                    skipPrompt,
                    requests = requests.Select(value => JsonDocument.Parse(value).RootElement.Clone()),
                    summaryCreated = saved.Tree.Entries.Any(node => node.Type == "branch_summary"),
                    targetSelected = saved.Tree.HeadId == target || saved.Tree.Entries.Last().ParentId == target,
                    settings = await File.ReadAllTextAsync(Path.Combine(agent, "settings.json"))
                }));
            }
        }
        finally
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            process?.Dispose();
            stop.Cancel();
            listener.Close();
            await server;
            Directory.Delete(root, recursive: true);
        }
    }
}
