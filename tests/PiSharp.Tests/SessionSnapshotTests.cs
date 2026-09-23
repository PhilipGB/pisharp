using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class SessionSnapshotTests
{
    [Fact]
    public async Task SaveRestoreAndContinuePreservesPreviousUserContext()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-session-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SessionSnapshots(dir, "fixture", "http://localhost:8000/v1", Path.Combine(dir, "snapshots"));
            var client = new HistoryClient();
            var agent = new PiAgent(client, new CodingTools(dir));
            var session = await agent.CreateSessionAsync();
            await foreach (var _ in agent.RunStreamingAsync("first turn", session)) { }
            var path = store.NewPath();
            await store.SaveAsync(agent, session, path);
            Assert.Equal(path, store.MostRecentPath());
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            var resumedClient = new HistoryClient();
            var resumedAgent = new PiAgent(resumedClient, new CodingTools(dir));
            var resumed = await store.LoadAsync(resumedAgent, path);
            await foreach (var _ in resumedAgent.RunStreamingAsync("second turn", resumed)) { }
            Assert.Contains("first turn", resumedClient.LatestRequest);
            Assert.Contains("second turn", resumedClient.LatestRequest);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new SessionSnapshots(dir, "other-model", "http://localhost:8000/v1", store.DirectoryPath).LoadAsync(resumedAgent, path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CliRejectsConflictingSessionFlagsAndAllowsLiteralDashPrompt()
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--continue", "--no-session"]));
        Assert.Equal("--help", CliArguments.Parse(["--", "--help"]).Prompt);
        Assert.Equal("example.json", CliArguments.Parse(["--session", "example.json"]).SessionPath);
    }

    private sealed class HistoryClient : IChatClient
    {
        public string LatestRequest { get; private set; } = "";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LatestRequest = string.Join(" ", messages.Select(m => m.Text));
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ack");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
