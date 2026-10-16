using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Protocols;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
using static PiSharp.Tests.RpcTestInputOutput;

namespace PiSharp.Tests;

public sealed class RpcResourceInvocationTests
{
    [Theory]
    [InlineData("prompt")]
    [InlineData("steer")]
    [InlineData("follow_up")]
    public async Task FailedSkillReadReportsExtensionErrorBeforeAcceptingOriginalInput(string command)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-rpc-resource-input-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "guide", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var commands = Channel.CreateUnbounded<string>();
        using var output = new LockedWriter();
        Task? serving = null;
        try
        {
            await File.WriteAllTextAsync(path, "---\nname: guide\ndescription: Guide\n---\nbody");
            var resources = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false,
                discoverPrompts: false, additionalSkills: [path]);
            File.Delete(path);
            var client = new ReplyClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root)),
                new ConversationSession(root, "fixture", null));
            serving = new RpcMode(new CommandReader(commands.Reader), output, run, resources: resources).ServeAsync();
            commands.Writer.TryWrite(JsonSerializer.Serialize(new { id = "input", type = command, message = "/skill:guide args" }));
            await WaitForAsync(output, command == "prompt" ? "agent_settled" : "\"id\":\"input\"");
            var lines = output.Lines().ToList();
            var errorIndex = lines.FindIndex(line => line.Contains("\"type\":\"extension_error\"", StringComparison.Ordinal));
            var responseIndex = lines.FindIndex(line => line.Contains("\"id\":\"input\"", StringComparison.Ordinal));
            Assert.True(errorIndex >= 0 && errorIndex < responseIndex, string.Join(Environment.NewLine, lines));
            using var error = JsonDocument.Parse(lines[errorIndex]);
            Assert.Equal(path, error.RootElement.GetProperty("extensionPath").GetString());
            Assert.Equal("skill_expansion", error.RootElement.GetProperty("event").GetString());
            Assert.Equal($"ENOENT: no such file or directory, open '{path}'", error.RootElement.GetProperty("error").GetString());
            using var response = JsonDocument.Parse(lines[responseIndex]);
            Assert.True(response.RootElement.GetProperty("success").GetBoolean());
            if (command == "prompt") Assert.Equal("/skill:guide args", client.UserText);
            else Assert.Equal("/skill:guide args", Assert.Single(run.GetPendingPrompts().InDeliveryOrder));
        }
        finally
        {
            commands.Writer.TryComplete();
            if (serving is not null) await serving.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ReplyClient : IChatClient
    {
        internal string? UserText { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "reply")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            UserText = messages.Last(m => m.Role == ChatRole.User).Text;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
