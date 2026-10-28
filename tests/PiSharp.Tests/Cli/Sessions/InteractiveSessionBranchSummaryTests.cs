using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class InteractiveSessionBranchSummaryTests
{
    [Fact]
    public async Task BranchCommandCanRequestAnExtensionSummary()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-branch-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var conversation = new ConversationSession(workingDirectory, "fixture-model", null);
            conversation.Append(new ChatMessage(ChatRole.User, "shared prompt"));
            var common = conversation.Tree.HeadId!;
            conversation.Append(new ChatMessage(ChatRole.Assistant, "abandoned branch"));
            var abandoned = conversation.Tree.HeadId!;
            conversation.Tree.Select(common);
            conversation.Append(new ChatMessage(ChatRole.User, "target branch"));
            var target = conversation.Tree.HeadId!;
            conversation.Tree.Select(abandoned);

            PiSharpExtensionSessionBeforeTreeEvent? beforeTree = null;
            var registration = new ExtensionRegistration();
            var adapter = new NativeExtensionRegistrationAdapter(registration);
            adapter.AddSessionBeforeTreeHandler((eventData, _, _) =>
            {
                beforeTree = eventData;
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeTreeResult?>(new(Summary: "kept branch context"));
            });
            adapter.Close();
            var run = await ConversationRun.OpenAsync(new PiAgent(new PassiveClient(), new CodingTools(workingDirectory),
                noBuiltinTools: true, liveExtensionRegistration: registration), conversation);
            string? draftPrompt = null;
            var commands = new InteractiveSessionCommands(new ConversationStore(workingDirectory), workingDirectory,
                false, new StringWriter(), () => Task.FromResult<string?>(null), () => { },
                new BranchSummarySettings(ReserveTokens: 1000), draft => draftPrompt = draft);

            var handled = await commands.TryExecuteAsync("/branch", $"--summarize {target[..12]}",
                conversation, run, sessionPath: null);

            Assert.True(handled);
            Assert.True(beforeTree?.UserWantsSummary);
            Assert.Equal(common, conversation.Tree.ActivePath().Last(node => node.Type != "branch_summary").Id);
            Assert.Equal("target branch", draftPrompt);
            Assert.Contains("kept branch context", conversation.ContextMessages().Last().Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private sealed class PassiveClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
