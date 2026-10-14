using System.Diagnostics;
using System.Net;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class PromptResourceProcessTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task SelectedPromptSourcesReachTheActualCliProviderRequest(bool trusted, bool explicitOverride, bool large)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-prompt-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        var (listener, port) = TestLoopbackListener.Start();
        using (listener)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            try
            {
                var selectedSystem = large ? "EXPLICIT BEGIN\n" + new string('x', 65 * 1024) + "\nEXPLICIT END" : "EXPLICIT SYSTEM";
                await File.WriteAllTextAsync(Path.Combine(agent, "SYSTEM.md"), "GLOBAL SYSTEM");
                await File.WriteAllTextAsync(Path.Combine(agent, "APPEND_SYSTEM.md"), "GLOBAL APPEND");
                await File.WriteAllTextAsync(Path.Combine(root, ".pi", "SYSTEM.md"), "PROJECT SYSTEM");
                await File.WriteAllTextAsync(Path.Combine(root, ".pi", "APPEND_SYSTEM.md"), "PROJECT APPEND");
                await File.WriteAllTextAsync(Path.Combine(root, "explicit.txt"), selectedSystem);
                if (explicitOverride && OperatingSystem.IsLinux())
                    foreach (var name in new[] { "SYSTEM.md", "APPEND_SYSTEM.md" })
                        File.SetUnixFileMode(Path.Combine(root, ".pi", name), UnixFileMode.None);
                await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), JsonSerializer.Serialize(new
                {
                    providers = new Dictionary<string, object>
                    {
                        ["fixture"] = new
                        {
                            baseUrl = $"http://127.0.0.1:{port}/v1",
                            apiKeyEnv = "PISHARP_PROMPT_FIXTURE_KEY",
                            models = new[] { new { id = "fixture-model" } }
                        }
                    }
                }));
                var server = Task.Run(async () =>
                {
                    var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync(deadline.Token);
                    Assert.Equal("Bearer fixture-key", context.Request.Headers["Authorization"]);
                    context.Response.ContentType = "text/event-stream";
                    await using (var writer = new StreamWriter(context.Response.OutputStream))
                    {
                        await writer.WriteAsync("data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"done\"},\"finish_reason\":null}]}\n\n");
                        await writer.WriteAsync("data: {\"id\":\"fixture\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"fixture-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                        await writer.FlushAsync(deadline.Token);
                    }
                    context.Response.Close();
                    return body;
                });
                var start = new ProcessStartInfo("dotnet")
                {
                    WorkingDirectory = root,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[] { typeof(CliArguments).Assembly.Location, "--provider", "fixture", "--model", "fixture-model",
                    trusted ? "--approve" : "--no-approve", "--no-session", "--no-tools", "--offline", "--no-extensions", "--no-skills",
                    "--no-prompt-templates", "--no-context-files", "--print", "hello" }) start.ArgumentList.Add(argument);
                if (explicitOverride)
                    foreach (var argument in new[] { "--system-prompt", "explicit.txt", "--append-system-prompt", "EXPLICIT APPEND" })
                        start.ArgumentList.Add(argument);
                foreach (var name in new[] { "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_MODEL", "PISHARP_SETTINGS_PATH",
                    "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) start.Environment.Remove(name);
                start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
                start.Environment["PISHARP_AGENT_DIR"] = agent;
                start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(root, "sessions");
                start.Environment["PISHARP_PROMPT_FIXTURE_KEY"] = "fixture-key";
                using var process = Process.Start(start)!;
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                    Assert.Equal(0, process.ExitCode);
                    Assert.Equal("done\n", await output);
                    Assert.Equal("", await error);
                    using var json = JsonDocument.Parse(await server.WaitAsync(deadline.Token));
                    var instructions = string.Join('\n', json.RootElement.GetProperty("messages").EnumerateArray()
                        .Where(message => message.GetProperty("role").GetString() is "system" or "developer")
                        .Select(message => message.GetProperty("content").GetString()));
                    Assert.Contains(explicitOverride ? selectedSystem : trusted ? "PROJECT SYSTEM" : "GLOBAL SYSTEM", instructions, StringComparison.Ordinal);
                    Assert.Contains(explicitOverride ? "EXPLICIT APPEND" : trusted ? "PROJECT APPEND" : "GLOBAL APPEND", instructions, StringComparison.Ordinal);
                    if (trusted) Assert.DoesNotContain("GLOBAL", instructions, StringComparison.Ordinal);
                    if (!trusted || explicitOverride) Assert.DoesNotContain("PROJECT", instructions, StringComparison.Ordinal);
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
