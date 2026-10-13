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
    public void ClonePathCopiesOnlyAncestorsThroughSpecifiedHead()
    {
        var tree = new ConversationTree();
        var root = tree.Append("user", Text("first"));
        tree.Append("assistant", Text("answer"));
        tree.Append("user", Text("later"));
        var copy = tree.ClonePath(root.Id);
        Assert.Equal([root.Id], copy.Entries.Select(item => item.Id));
        Assert.Equal(root.Id, copy.HeadId);
        Assert.Empty(tree.ClonePath(null).Entries);
        Assert.Throws<KeyNotFoundException>(() => tree.ClonePath("missing"));
        Assert.Equal(3, tree.Entries.Count);
    }

    [Fact]
    public void ClonePreservesAllBranchesAndSelectedHead()
    {
        var tree = new ConversationTree();
        var root = tree.Append("user", Text("first"));
        var earlierBranch = tree.Append("assistant", Text("earlier"));
        tree.Select(root.Id);
        var activeBranch = tree.Append("assistant", Text("active"));

        var copy = tree.Clone();

        Assert.Equal(tree.Entries.Select(entry => entry.Id), copy.Entries.Select(entry => entry.Id));
        Assert.Equal(activeBranch.Id, copy.HeadId);
        Assert.Equal([root.Id, activeBranch.Id], copy.ActivePath().Select(entry => entry.Id));
        Assert.Equal(earlierBranch.Id, copy.Entries[1].Id);
        copy.Append("user", Text("copy only"));
        Assert.Equal(3, tree.Entries.Count);
        Assert.Equal(activeBranch.Id, tree.HeadId);
    }

    [Fact]
    public void RejectsDanglingParentsAndDuplicateIds()
    {
        var node = new ConversationNode("id", "missing", "user", Text("hello"), DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(() => new ConversationTree([node]));
        var root = node with { ParentId = null };
        Assert.Throws<InvalidDataException>(() => new ConversationTree([root, root]));
    }

    [Fact]
    public void ImportedEntriesKeepPhysicalOrderAndResolveForwardParents()
    {
        var child = new ConversationNode("child", "parent", "chat", Text("child"), DateTimeOffset.UtcNow);
        var parent = new ConversationNode("parent", null, "chat", Text("parent"), DateTimeOffset.UtcNow);

        var tree = ConversationTree.FromEntries([child, parent], child.Id);

        Assert.Equal([child.Id, parent.Id], tree.Entries.Select(entry => entry.Id));
        Assert.Equal([parent.Id, child.Id], tree.ActivePath().Select(entry => entry.Id));
    }

    [Fact]
    public void ImportedEntriesRejectParentCycles()
    {
        var first = new ConversationNode("first", "second", "chat", Text("first"), DateTimeOffset.UtcNow);
        var second = new ConversationNode("second", "first", "chat", Text("second"), DateTimeOffset.UtcNow);

        Assert.Throws<InvalidDataException>(() => ConversationTree.FromEntries([first, second]));
    }

    [Fact]
    public async Task ConcurrentAppendsRemainOneValidSelectedPath()
    {
        var tree = new ConversationTree();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 100; index++)
                tree.Append("chat", Text($"{worker}:{index}"));
        })));

        var entries = tree.Entries;
        var path = tree.ActivePath();
        Assert.Equal(800, entries.Count);
        Assert.Equal(entries.Select(entry => entry.Id), path.Select(entry => entry.Id));
        Assert.Equal(entries[^1].Id, tree.HeadId);
        Assert.All(entries.Zip(entries.Skip(1)), pair => Assert.Equal(pair.First.Id, pair.Second.ParentId));
    }
}
