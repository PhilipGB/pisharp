using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ProjectTrustTests
{
    [Fact]
    public async Task ProtectedPromptsRequireTrustButContextInstructionsDoNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-trust-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(root, "project");
        var child = Path.Combine(cwd, "child");
        var agentDir = Path.Combine(root, "agent");
        Directory.CreateDirectory(Path.Combine(cwd, ".pi"));
        Directory.CreateDirectory(child);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cwd, ".pi", "SYSTEM.md"), "TRUSTED SYSTEM");
            await File.WriteAllTextAsync(Path.Combine(cwd, "AGENTS.md"), "UNTRUSTED CONTEXT");
            var trust = new ProjectTrust(agentDir);
            Assert.True(ProjectTrust.HasProtectedResources(cwd));
            Assert.False(await trust.ResolveAsync(cwd, null, false, TextReader.Null, TextWriter.Null));
            var denied = await ProjectPrompts.LoadAsync(cwd, agentDir, false);
            Assert.Null(denied.System);
            var context = await ContextInstructions.LoadAsync(cwd, agentDir);
            Assert.Contains("UNTRUSTED CONTEXT", context);
            var client = new CaptureClient();
            var agent = new PiAgent(client, new CodingTools(cwd), noTools: true,
                contextInstructions: context, systemPrompt: denied.System);
            await foreach (var _ in agent.RunStreamingAsync("hi", await agent.CreateSessionAsync())) { }
            Assert.DoesNotContain("TRUSTED SYSTEM", client.Instructions);
            Assert.Contains("UNTRUSTED CONTEXT", client.Instructions);
            await trust.SetAsync(cwd, true);
            Assert.True(await new ProjectTrust(agentDir).GetAsync(child));
            var trusted = await ProjectPrompts.LoadAsync(cwd, agentDir, true);
            Assert.Equal("TRUSTED SYSTEM", trusted.System);
            await trust.SetAsync(child, false);
            Assert.False(await trust.GetAsync(child));
            Assert.True(await trust.GetAsync(cwd));
            await trust.SetAsync(child, null);
            Assert.True(await trust.GetAsync(child));
            Assert.False(await trust.ResolveAsync(cwd, false, false, TextReader.Null, TextWriter.Null));
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(trust.PathOnDisk));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InteractiveDecisionCanBeSavedAndMalformedStoreFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi", "extensions"));
        try
        {
            var trust = new ProjectTrust(Path.Combine(root, "agent"));
            Assert.True(await trust.ResolveAsync(root, null, true, new StringReader("yes\n"), new StringWriter()));
            Assert.True(await trust.ResolveAsync(root, null, false, TextReader.Null, TextWriter.Null));
            await File.WriteAllTextAsync(trust.PathOnDisk, "{\"/tmp\":null}");
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => trust.GetAsync(root));
            Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--approve", "--no-approve"]));
            Assert.True(CliArguments.Parse(["--approve"]).ProjectTrustOverride);
            Assert.False(CliArguments.Parse(["--no-approve"]).ProjectTrustOverride);
            Assert.True(CliArguments.Parse(["-a"]).ProjectTrustOverride);
            Assert.False(CliArguments.Parse(["-na"]).ProjectTrustOverride);
            Assert.Throws<ArgumentException>(() => CliArguments.Parse(["-a", "-na"]));
            Assert.Equal("interactive", CliArguments.Parse(["--mode", "text"]).Mode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UserDefaultTrustAppliesOnlyWithoutStoredDecisionAndNeverOverridesCli()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-default-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), "{}");
            var trust = new ProjectTrust(Path.Combine(root, "agent"));
            Assert.True(await trust.ResolveAsync(root, null, false, TextReader.Null, TextWriter.Null,
                defaultProjectTrust: "always"));
            Assert.False(await trust.ResolveAsync(root, null, false, TextReader.Null, TextWriter.Null,
                defaultProjectTrust: "never"));
            Assert.False(await trust.ResolveAsync(root, null, false, TextReader.Null, TextWriter.Null));
            await trust.SetAsync(root, false);
            Assert.False(await trust.ResolveAsync(root, null, false, TextReader.Null, TextWriter.Null,
                defaultProjectTrust: "always"));
            Assert.True(await trust.ResolveAsync(root, true, false, TextReader.Null, TextWriter.Null,
                defaultProjectTrust: "never"));
            await trust.SetAsync(root, true);
            Assert.True(await trust.ResolveAsync(root, null, false, TextReader.Null, TextWriter.Null,
                defaultProjectTrust: "never"));
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
