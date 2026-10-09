using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ContextInstructionsTests
{
    [Fact]
    public async Task LoadsNestedLinkedWorktreeInstructionsOnceWhenTheySymlinkToTheMainRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-worktree-" + Guid.NewGuid().ToString("N"));
        var (main, worktree, child) = await CreateNestedLinkedWorktree(root);
        try
        {
            var mainInstructions = Path.Combine(main, "AGENTS.md");
            await File.WriteAllTextAsync(mainInstructions, "SHARED WORKTREE RULE");
            File.CreateSymbolicLink(Path.Combine(worktree, "AGENTS.md"), mainInstructions);

            var text = await ContextInstructions.LoadAsync(child, Path.Combine(root, "user"));

            Assert.Equal(1, CountOccurrences(text, "SHARED WORKTREE RULE"));
            Assert.Contains(Path.Combine(worktree, "AGENTS.md"), text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LoadsNestedLinkedWorktreeInstructionsOnceWhenBothFilesSymlinkToSharedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-worktree-" + Guid.NewGuid().ToString("N"));
        var (main, worktree, child) = await CreateNestedLinkedWorktree(root);
        try
        {
            var sharedInstructions = Path.Combine(root, "shared-agents.md");
            await File.WriteAllTextAsync(sharedInstructions, "SHARED WORKTREE RULE");
            File.CreateSymbolicLink(Path.Combine(main, "AGENTS.md"), sharedInstructions);
            File.CreateSymbolicLink(Path.Combine(worktree, "AGENTS.md"), sharedInstructions);

            var text = await ContextInstructions.LoadAsync(child, Path.Combine(root, "user"));

            Assert.Equal(1, CountOccurrences(text, "SHARED WORKTREE RULE"));
            Assert.Contains(Path.Combine(worktree, "AGENTS.md"), text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NestedLinkedWorktreeInstructionsShadowTheMainRepoFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-worktree-" + Guid.NewGuid().ToString("N"));
        var (main, worktree, child) = await CreateNestedLinkedWorktree(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(main, "AGENTS.md"), "MAIN REPO RULE");
            await File.WriteAllTextAsync(Path.Combine(worktree, "AGENTS.md"), "WORKTREE RULE");

            var text = await ContextInstructions.LoadAsync(child, Path.Combine(root, "user"));

            Assert.Contains("WORKTREE RULE", text);
            Assert.DoesNotContain("MAIN REPO RULE", text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NestedLinkedWorktreeWithoutItsOwnInstructionsInheritsTheMainRepoFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-worktree-" + Guid.NewGuid().ToString("N"));
        var (main, _, child) = await CreateNestedLinkedWorktree(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(main, "AGENTS.md"), "INHERITED MAIN RULE");

            var text = await ContextInstructions.LoadAsync(child, Path.Combine(root, "user"));

            Assert.Equal(1, CountOccurrences(text, "INHERITED MAIN RULE"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task NestedLinkedWorktreeOnlyShadowsTheSameContextFilename()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-worktree-" + Guid.NewGuid().ToString("N"));
        var (main, worktree, child) = await CreateNestedLinkedWorktree(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(main, "CLAUDE.md"), "MAIN CLAUDE RULE");
            await File.WriteAllTextAsync(Path.Combine(worktree, "AGENTS.md"), "WORKTREE AGENTS RULE");

            var text = await ContextInstructions.LoadAsync(child, Path.Combine(root, "user"));

            Assert.Contains("MAIN CLAUDE RULE", text);
            Assert.Contains("WORKTREE AGENTS RULE", text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BareRepositoryLayoutDoesNotSuppressContainerInstructions()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-bare-worktree-" + Guid.NewGuid().ToString("N"));
        var bare = Path.Combine(root, ".bare");
        var worktree = Path.Combine(root, "main");
        var child = Path.Combine(worktree, "src");
        var worktreeGitDir = Path.Combine(bare, "worktrees", "main");
        Directory.CreateDirectory(worktreeGitDir);
        Directory.CreateDirectory(child);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(bare, "HEAD"), "ref: refs/heads/main\n");
            await File.WriteAllTextAsync(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/main\n");
            await File.WriteAllTextAsync(Path.Combine(worktreeGitDir, "commondir"), "../..\n");
            await File.WriteAllTextAsync(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "CONTAINER RULE");
            await File.WriteAllTextAsync(Path.Combine(worktree, "AGENTS.md"), "BARE WORKTREE RULE");

            var text = await ContextInstructions.LoadAsync(worktree, Path.Combine(root, "user"));

            Assert.Contains("CONTAINER RULE", text);
            Assert.Contains("BARE WORKTREE RULE", text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task SiblingLinkedWorktreeDoesNotSuppressAncestorInstructions()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-context-sibling-worktree-" + Guid.NewGuid().ToString("N"));
        var main = Path.Combine(root, "main");
        var worktree = Path.Combine(root, "feature");
        var child = Path.Combine(worktree, "src");
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "feature");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(worktreeGitDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(main, ".git", "HEAD"), "ref: refs/heads/main\n");
            await File.WriteAllTextAsync(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/feature\n");
            await File.WriteAllTextAsync(Path.Combine(worktreeGitDir, "commondir"), "../..\n");
            await File.WriteAllTextAsync(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");
            await File.WriteAllTextAsync(Path.Combine(root, "AGENTS.md"), "OUTER RULE");
            await File.WriteAllTextAsync(Path.Combine(worktree, "AGENTS.md"), "SIBLING WORKTREE RULE");

            var text = await ContextInstructions.LoadAsync(child, Path.Combine(root, "user"));

            Assert.Contains("OUTER RULE", text);
            Assert.Contains("SIBLING WORKTREE RULE", text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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
            var loadedFiles = new List<string>();
            var text = await ContextInstructions.LoadAsync(child, agentDir, loadedFiles: loadedFiles);
            Assert.True(text.IndexOf("USER RULE", StringComparison.Ordinal) < text.IndexOf("PROJECT OVERRIDE", StringComparison.Ordinal));
            Assert.True(text.IndexOf("PROJECT OVERRIDE", StringComparison.Ordinal) < text.IndexOf("CHILD RULE", StringComparison.Ordinal));
            Assert.DoesNotContain("old rule", text);
            Assert.Equal([Path.Combine(agentDir, "CLAUDE.md"), Path.Combine(project, "AGENTS.override.md"),
                Path.Combine(child, "AGENTS.MD")], loadedFiles);
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

    private static async Task<(string Main, string Worktree, string Child)> CreateNestedLinkedWorktree(string root)
    {
        var main = Path.Combine(root, "main");
        var worktree = Path.Combine(main, "worktrees", "feature");
        var child = Path.Combine(worktree, "src");
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "feature");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(worktreeGitDir);
        await File.WriteAllTextAsync(Path.Combine(main, ".git", "HEAD"), "ref: refs/heads/main\n");
        await File.WriteAllTextAsync(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/feature\n");
        await File.WriteAllTextAsync(Path.Combine(worktreeGitDir, "commondir"), "../..\n");
        await File.WriteAllTextAsync(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}\n");
        return (main, worktree, child);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }
}
