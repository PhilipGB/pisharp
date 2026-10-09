using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ProjectTrustTests
{
    [Fact]
    public async Task ProjectPromptSourcesFollowTrustAndSystemAppendPrecedence()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-prompt-source-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var agent = Path.Combine(root, "agent");
        Directory.CreateDirectory(Path.Combine(project, ".pi"));
        Directory.CreateDirectory(agent);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(agent, "SYSTEM.md"), "USER SYSTEM");
            await File.WriteAllTextAsync(Path.Combine(agent, "APPEND_SYSTEM.md"), "USER APPEND");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "SYSTEM.md"), "PROJECT SYSTEM");
            await File.WriteAllTextAsync(Path.Combine(project, ".pi", "APPEND_SYSTEM.md"), "PROJECT APPEND");

            var untrusted = await ProjectPrompts.LoadWithSourcesAsync(project, agent, projectTrusted: false);
            Assert.Equal("USER SYSTEM", untrusted.System);
            Assert.Equal(Path.Combine(agent, "SYSTEM.md"), untrusted.SystemPath);
            Assert.Equal("USER APPEND", untrusted.Append);
            Assert.Equal([Path.Combine(agent, "APPEND_SYSTEM.md")], untrusted.AppendPaths);

            var trusted = await ProjectPrompts.LoadWithSourcesAsync(project, agent, projectTrusted: true);
            Assert.Equal("PROJECT SYSTEM", trusted.System);
            Assert.Equal(Path.Combine(project, ".pi", "SYSTEM.md"), trusted.SystemPath);
            Assert.Equal("USER APPEND\n\nPROJECT APPEND", trusted.Append);
            Assert.Equal([Path.Combine(agent, "APPEND_SYSTEM.md"), Path.Combine(project, ".pi", "APPEND_SYSTEM.md")],
                trusted.AppendPaths);
        }
        finally { Directory.Delete(root, true); }
    }

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

    [Fact]
    public async Task TrustDecisionsUseTheCanonicalPathForSymbolicLinkAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-trust-link-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var alias = Path.Combine(root, "project-alias");
        Directory.CreateDirectory(project);
        Directory.CreateSymbolicLink(alias, project);
        try
        {
            var trust = new ProjectTrust(Path.Combine(root, "agent"));
            await trust.SetAsync(project, true);

            Assert.True(await trust.GetAsync(alias));

            await trust.SetAsync(alias, false);
            Assert.False(await trust.GetAsync(project));
            using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(trust.PathOnDisk));
            Assert.Equal(new[] { Path.GetFullPath(project) }, saved.RootElement.EnumerateObject()
                .Select(property => property.Name));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TrustParentDecisionReplacesProjectOverrideAtomically()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-trust-parent-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "parent");
        var project = Path.Combine(parent, "project");
        Directory.CreateDirectory(project);
        try
        {
            var trust = new ProjectTrust(Path.Combine(root, "agent"));
            await trust.SetAsync(parent, true);
            await trust.SetAsync(project, false);

            Assert.Equal(new ProjectTrustEntry(project, false), await trust.GetEntryAsync(project));

            await trust.SetManyAsync([
                new ProjectTrustUpdate(parent, true),
                new ProjectTrustUpdate(project, null)
            ]);

            Assert.Equal(new ProjectTrustEntry(parent, true), await trust.GetEntryAsync(project));
            using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(trust.PathOnDisk));
            Assert.Single(saved.RootElement.EnumerateObject());
            Assert.True(saved.RootElement.GetProperty(Path.GetFullPath(parent)).GetBoolean());
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
