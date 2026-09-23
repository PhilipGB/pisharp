using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Tests;

public sealed class ConversationTreeTests
{
    private static JsonElement Text(string text) => JsonSerializer.SerializeToElement(new { text });

    [Fact]
    public void NavigatingAndBranchingKeepsPriorDescendantsOutOfActiveContext()
    {
        var tree = new ConversationTree();
        var first = tree.Append("user", Text("question"));
        var oldAnswer = tree.Append("assistant", Text("answer A"));
        tree.Select(first.Id);
        var second = tree.Append("assistant", Text("answer B"));
        Assert.Equal(3, tree.Entries.Count);
        Assert.Equal([first.Id, second.Id], tree.ActivePath().Select(e => e.Id));
        tree.Select(oldAnswer.Id);
        Assert.Equal([first.Id, oldAnswer.Id], tree.ActivePath().Select(e => e.Id));
        var clone = tree.CloneActivePath();
        Assert.Equal(2, clone.Entries.Count);
        Assert.Equal(oldAnswer.Id, clone.HeadId);
        Assert.Equal(3, tree.Entries.Count);
    }

    [Fact]
    public void RejectsDanglingParentsAndDuplicateIds()
    {
        var node = new ConversationNode("id", "missing", "user", Text("hello"), DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(() => new ConversationTree([node]));
        var root = node with { ParentId = null };
        Assert.Throws<InvalidDataException>(() => new ConversationTree([root, root]));
    }
}
