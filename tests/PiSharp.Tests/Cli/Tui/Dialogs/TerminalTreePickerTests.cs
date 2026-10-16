using Microsoft.Extensions.AI;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class TerminalTreePickerTests
{
    [Fact]
    public void TreeOptionsShowParentStructurePreviewsAndTheSelectedHead()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "test-model", null);
        conversation.Append(new ChatMessage(ChatRole.User, "first prompt"));
        var promptId = conversation.Tree.HeadId!;
        conversation.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
        var firstAnswerId = conversation.Tree.HeadId!;
        conversation.Append(new ChatMessage(ChatRole.User, "second prompt"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "second answer"));
        var latestId = conversation.Tree.HeadId!;
        conversation.Tree.Select(promptId);
        conversation.Append(new ChatMessage(ChatRole.Assistant, "alternate answer"));

        var options = TerminalTreePicker.ToOptions(conversation.Tree.Entries, conversation.Tree.HeadId);

        Assert.Collection(options,
            option => Assert.Contains("user: first prompt", option.Label),
            option => Assert.Contains("assistant: first answer", option.Label),
            option => Assert.Contains("user: second prompt", option.Label),
            option => Assert.Contains("assistant: second answer", option.Label),
            option => Assert.Contains("assistant: alternate answer", option.Label));
        Assert.Contains("├─", options.Single(option => option.Key == firstAnswerId).Label);
        Assert.Contains("└─", options.Single(option => option.Key == conversation.Tree.HeadId).Label);
        Assert.False(options.Single(option => option.Key == latestId).IsCurrent);
        Assert.True(options.Single(option => option.Key == conversation.Tree.HeadId).IsCurrent);
        Assert.Contains(firstAnswerId, options.Single(option => option.Key == firstAnswerId).SearchText);
    }
}
