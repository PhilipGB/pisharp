using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class CodemodeBashOutputTests
{
    [Theory]
    [InlineData(100000, 7, false)]
    [InlineData(1048567, 0, false)]
    [InlineData(1100000, 0, true)]
    public async Task ScriptGetsBoundedStructuredBashOutputAndNonzeroExitStillResolves(int bytes, int exitCode, bool truncated)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-codemode-bash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new List<string>();
        try
        {
            var registry = new ExtensionRegistration();
            CodemodeBuiltin.Configure(registry);
            var command = $"printf start; printf '%{bytes}s' '' | tr ' ' a; printf tail; exit {exitCode}";
            var code = "const r=await tools.bash({command:" + System.Text.Json.JsonSerializer.Serialize(command) + "});" +
                "text([r.output.length,r.truncated,r.exit_code,r.output.startsWith('start'),r.output.endsWith('tail'),typeof r.wall_time_seconds,typeof r.full_output_path]);";
            var client = new ScriptClient(code);
            var session = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root),
                selectedTools: ["bash", "codemode"], extensionToolRegistrations: registry.ToolDefinitions), session,
                save: _ => Task.CompletedTask);
            var events = new List<AgentLifecycleEvent>();
            await foreach (var item in run.RunEventsAsync("inspect Bash")) events.Add(item);
            var bash = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == "bash");
            if (bash.Details is { } rawDetails && System.Text.Json.JsonSerializer.SerializeToElement(rawDetails).TryGetProperty("fullOutputPath", out var fullPath) && fullPath.ValueKind == System.Text.Json.JsonValueKind.String)
                paths.Add(fullPath.GetString()!);
            var script = Assert.Single(events, item => item.Type == "tool_execution_finished" && item.Tool == "codemode");
            Assert.False(script.IsError);
            Assert.Equal(exitCode != 0, bash.IsError);
            var output = bash.StructuredContent!.Value;
            Assert.Equal(truncated, output.GetProperty("truncated").GetBoolean());
            Assert.Equal(exitCode, output.GetProperty("exit_code").GetInt32());
            var content = output.GetProperty("output").GetString()!;
            Assert.StartsWith("start", content);
            Assert.EndsWith("tail", content);
            Assert.DoesNotContain('\uFFFD', content);
            if (truncated)
            {
                Assert.InRange(content.Length, 1024 * 1024, 1024 * 1024 + 100);
                Assert.Contains($"[... {bytes + 9 - 1024 * 1024} bytes omitted ...]", content);
                var path = output.GetProperty("full_output_path").GetString()!;
                Assert.Equal(bytes + 9, new FileInfo(path).Length);
                if (!paths.Contains(path)) paths.Add(path);
            }
            else
            {
                Assert.Equal(bytes + 9, content.Length);
                Assert.False(output.TryGetProperty("full_output_path", out _));
            }
            Assert.Contains(truncated ? "true" : "false", script.Text);
            Assert.Contains("true,true,\"number\"", script.Text);
            Assert.True(bash.Text!.Length < 52 * 1024);
        }
        finally
        {
            foreach (var path in paths) File.Delete(path);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StructuredTruncationPreservesUtf8BoundariesAndExactCap()
    {
        await using var buffer = new ShellOutputBuffer();
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("🙂", 300000)));
        await buffer.AppendAsync(bytes);
        var display = await buffer.FinishWithMetadataAsync();
        try
        {
            var output = await buffer.ReadFullOutputAsync(1024 * 1024 - 2);
            Assert.True(output.Truncated);
            Assert.DoesNotContain('�', output.Content);
            Assert.StartsWith("🙂", output.Content);
            Assert.EndsWith("🙂", output.Content);
            Assert.Contains("[... 151426 bytes omitted ...]", output.Content);
        }
        finally { if (display.FullOutputPath is { } path) File.Delete(path); }
    }

    private sealed class ScriptClient(string code) : IChatClient
    {
        private int _requests;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 1)
                yield return new(ChatRole.Assistant, [new FunctionCallContent("script", "codemode", new Dictionary<string, object?> { ["code"] = code })]);
            else yield return new(ChatRole.Assistant, "done");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
