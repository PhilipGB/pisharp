using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class SessionDocumentTests
{
    [Fact]
    public void GetActivePath_ReconstructsSelectedBranch()
    {
        using var temp = TempDirectory.Create();
        var header = SessionHeader.Create(temp.Path, "model");
        var root = Turn(null, "root");
        var left = Turn(root.Id, "left");
        var right = Turn(root.Id, "right");
        var leaf = Turn(left.Id, "leaf");

        var document = new SessionDocument(
            Path.Combine(temp.Path, "session.jsonl"),
            header,
            [root, left, right, leaf]);

        var path = document.GetActivePath(leaf.Id);

        Assert.Equal(new[] { root.Id, left.Id, leaf.Id }, path.Select(turn => turn.Id));
    }

    [Fact]
    public void ResolveTurn_RejectsAmbiguousPrefix()
    {
        using var temp = TempDirectory.Create();
        var header = SessionHeader.Create(temp.Path, "model");
        var first = Turn(null, "one") with { Id = "abc111" };
        var second = Turn(first.Id, "two") with { Id = "abc222" };
        var document = new SessionDocument(Path.Combine(temp.Path, "session.jsonl"), header, [first, second]);

        Assert.Throws<InvalidOperationException>(() => document.ResolveTurn("abc"));
    }

    [Fact]
    public void GetLabel_LatestChangeWinsAndClearsWhenBlank()
    {
        using var temp = TempDirectory.Create();
        var header = PiSessionHeader.Create(temp.Path);
        var target = Message("target", "hello");
        var first = new LabelEntry("l1", "target", DateTimeOffset.UtcNow, "target", "first");
        var second = new LabelEntry("l2", "l1", DateTimeOffset.UtcNow, "target", "second");
        var entries = new SessionEntry[] { target, first, second };
        var document = new SessionDocument(Path.Combine(temp.Path, "session.jsonl"), header, entries);

        Assert.Equal("second", document.GetLabel("target"));

        var clearedEntries = new SessionEntry[]
        {
            target,
            first,
            second,
            new LabelEntry("l3", "l2", DateTimeOffset.UtcNow, "target", " "),
        };
        var cleared = new SessionDocument(Path.Combine(temp.Path, "session.jsonl"), header, clearedEntries);

        Assert.Null(cleared.GetLabel("target"));
        Assert.Equal("second", document.GetLabel("target"));
    }

    [Fact]
    public void GetLabel_MatchesTargetCaseInsensitivelyAndReturnsNullForUnknownEntry()
    {
        using var temp = TempDirectory.Create();
        var header = PiSessionHeader.Create(temp.Path);
        var target = Message("target", "hello");
        var label = new LabelEntry("l1", "target", DateTimeOffset.UtcNow, "TARGET", "bookmark");
        var document = new SessionDocument(
            Path.Combine(temp.Path, "session.jsonl"),
            header,
            new SessionEntry[] { target, label });

        Assert.Equal("bookmark", document.GetLabel("target"));
        Assert.Null(document.GetLabel("missing"));
        Assert.Throws<ArgumentException>(() => document.GetLabel(""));
    }

    private static MessageEntry Message(string id, string text) => new(
        id,
        null,
        DateTimeOffset.UtcNow,
        JsonSerializer.SerializeToElement(new { role = "user", content = text }));

    private static SessionTurn Turn(string? parentId, string message)
    {
        using var json = JsonDocument.Parse("{\"state\":true}");
        return SessionTurn.Create(parentId, message, "assistant", json.RootElement);
    }
}
