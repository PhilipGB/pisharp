using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ContextInstructionsTests
{
    [Fact]
    public async Task LoadsGlobalThenAncestorThenOverrideAndReachesMaf()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-" + Guid.NewGuid().ToString("N"));
        var agentDir = Path.Combine(root, "user");
        var project = Path.Combine(root, "project");
        var child = Path.Combine(project, "nested");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(child);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agentDir, "CLAUDE.md"), "USER RULE");
            await File.WriteAllTextAsync(Path.Combine(project, "AGENTS.md"), "old rule");
            await File.WriteAllTextAsync(Path.Combine(project, "AGENTS.override.md"), "PROJECT OVERRIDE");
            await File.WriteAllTextAsync(Path.Combine(child, "AGENTS.MD"), "CHILD RULE");
            var text = await ContextInstructions.LoadAsync(child, agentDir);
            Assert.True(text.IndexOf("USER RULE", StringComparison.Ordinal) < text.IndexOf("PROJECT OVERRIDE", StringComparison.Ordinal));
            Assert.True(text.IndexOf("PROJECT OVERRIDE", StringComparison.Ordinal) < text.IndexOf("CHILD RULE", StringComparison.Ordinal));
            Assert.DoesNotContain("old rule", text);
            var provider = new CaptureClient();
            var agent = new PiAgent(provider, new CodingTools(child), noTools: true, contextInstructions: text);
            await foreach (var _ in agent.RunStreamingAsync("hi", await agent.CreateSessionAsync())) { }
            Assert.Contains("PROJECT OVERRIDE", provider.Instructions);
            Assert.Contains("CHILD RULE", provider.Instructions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RefusesOversizedFileWithoutSilentlyTruncatingInstructions()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), new string('x', 65 * 1024));
            await Assert.ThrowsAsync<InvalidDataException>(() => ContextInstructions.LoadAsync(root, Path.Combine(root, "user")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CaptureClient : IChatClient
    {
        public string? Instructions { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Instructions = options?.Instructions;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
