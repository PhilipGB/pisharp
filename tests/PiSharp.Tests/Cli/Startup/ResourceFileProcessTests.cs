using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class ResourceFileProcessTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public Task CompleteExpandedResourceReachesProviderAfterOptionalReadFailure(bool skill, bool unreadable) =>
        RunAsync(skill, unreadable, namedPipe: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task NamedPipeResourceDoesNotBlockCliExpansion(bool skill) => RunAsync(skill, unreadable: false, namedPipe: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task InvalidYamlResourceDoesNotBlockCliExpansion(bool skill) =>
        RunAsync(skill, unreadable: false, namedPipe: false, invalidYaml: true);

    private static async Task RunAsync(bool skill, bool unreadable, bool namedPipe, bool invalidYaml = false)
    {
        if ((unreadable || namedPipe) && OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-resource-file-process-" + Guid.NewGuid().ToString("N"));
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(agent);
        Directory.CreateDirectory(Path.Combine(root, "guide"));
        Directory.CreateDirectory(Path.Combine(root, "denied"));
        var path = Path.Combine(root, "guide", skill ? "SKILL.md" : "guide.md");
        var denied = Path.Combine(root, "denied", skill ? "SKILL.md" : "denied.md");
        var expected = "BEGIN\n" + new string('x', 66560) + "\ninvalid \uFFFD tail\nEND";
        var bytes = Encoding.UTF8.GetBytes("---\nname: guide\ndescription: Guide\n---\nBEGIN\n" + new string('x', 66560) + "\ninvalid ")
            .Concat(new byte[] { 0xff }).Concat(Encoding.UTF8.GetBytes(" tail\nEND")).ToArray();
        var (listener, port) = TestLoopbackListener.Start();
        using (listener)
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try
            {
                await File.WriteAllBytesAsync(path, bytes);
                if (namedPipe)
                {
                    var pipeStart = new ProcessStartInfo("mkfifo");
                    pipeStart.ArgumentList.Add(denied);
                    using var createPipe = Process.Start(pipeStart)!;
                    await createPipe.WaitForExitAsync(deadline.Token);
                    Assert.Equal(0, createPipe.ExitCode);
                }
                else await File.WriteAllTextAsync(denied, invalidYaml
                    ? "---\nname: denied\ndescription: Denied\nignored: [broken\n---\nunreadable resource"
                    : "---\nname: denied\ndescription: Denied\n---\nunreadable resource");
                if (unreadable && !OperatingSystem.IsWindows()) File.SetUnixFileMode(denied, UnixFileMode.None);
                await File.WriteAllTextAsync(Path.Combine(agent, "models.json"), JsonSerializer.Serialize(new
                {
                    providers = new Dictionary<string, object>
                    {
                        ["fixture"] = new
                        {
                            baseUrl = $"http://127.0.0.1:{port}/v1",
                            apiKeyEnv = "PISHARP_RESOURCE_FIXTURE_KEY",
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
                    "--no-approve", "--no-session", "--no-tools", "--offline", "--no-extensions", "--no-skills", "--no-prompt-templates",
                    "--no-context-files", "--print", skill ? "/skill:guide" : "/guide" }) start.ArgumentList.Add(argument);
                if (unreadable || namedPipe || invalidYaml) { start.ArgumentList.Add(skill ? "--skill" : "--prompt-template"); start.ArgumentList.Add(denied); }
                start.ArgumentList.Add(skill ? "--skill" : "--prompt-template");
                start.ArgumentList.Add(path);
                foreach (var name in new[] { "PISHARP_API_KEY", "PISHARP_BASE_URL", "PISHARP_AUTH_PATH", "PISHARP_MODELS_PATH", "PISHARP_MODEL",
                    "PISHARP_SETTINGS_PATH", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) start.Environment.Remove(name);
                start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
                start.Environment["PISHARP_AGENT_DIR"] = agent;
                start.Environment["PISHARP_SESSION_DIR"] = Path.Combine(root, "sessions");
                start.Environment["PISHARP_RESOURCE_FIXTURE_KEY"] = "fixture-key";
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
                    var user = json.RootElement.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user")
                        .GetProperty("content");
                    var text = user.ValueKind == JsonValueKind.String ? user.GetString()! : string.Concat(user.EnumerateArray().Select(p => p.GetProperty("text").GetString()));
                    Assert.Contains(expected, text, StringComparison.Ordinal);
                    Assert.DoesNotContain("unreadable resource", text, StringComparison.Ordinal);
                    if (!skill) Assert.Equal(expected, text);
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
