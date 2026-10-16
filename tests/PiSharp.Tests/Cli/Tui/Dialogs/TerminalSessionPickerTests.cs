using System.Text.Json;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class TerminalSessionPickerTests
{
    [Fact]
    public void SessionOptionShowsAndSearchesTheSelectedHeadAndBranchHeadCount()
    {
        var workingDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pisharp-picker-test"));
        var conversation = new ConversationSession(workingDirectory, "test-model", null);
        using var payload = JsonDocument.Parse("{}");
        var root = conversation.Tree.Append("chat", payload.RootElement);
        var currentHead = conversation.Tree.Append("chat", payload.RootElement);
        conversation.Tree.Select(root.Id);
        conversation.Tree.Append("chat", payload.RootElement);
        conversation.Tree.Select(currentHead.Id);

        var details = SessionPickerBranchHeads.From(conversation);
        var path = Path.Combine(workingDirectory, "current.session.json");
        var listing = new SessionListing(path, conversation.Id, "Current conversation", DateTimeOffset.UtcNow,
            3, "test-model", new string('a', 64));
        var otherListing = new SessionListing(Path.Combine(workingDirectory, "other.session.json"),
            "other-session", "Other conversation", DateTimeOffset.UtcNow, 1, "other-model", new string('b', 64));
        var otherDetails = new SessionPickerBranchHeads("abcdef0123456789", 1);

        var option = TerminalSessionPicker.ToOption(listing, path, details);
        var otherOption = TerminalSessionPicker.ToOption(otherListing, path, otherDetails);
        var list = new TerminalSelectionList<SessionListing>("Resume session", [option, otherOption], option.Key);
        list.HandleInput(new(null, otherDetails.HeadId));

        Assert.True(option.IsCurrent);
        Assert.Contains($"Current · head {currentHead.Id[..12]}", option.Description);
        Assert.Contains("2 branch heads", option.Description);
        Assert.Contains(currentHead.Id, option.SearchText);
        Assert.False(otherOption.IsCurrent);
        Assert.Equal("other-session", list.Selected?.Option.Value.Id);
    }
}
