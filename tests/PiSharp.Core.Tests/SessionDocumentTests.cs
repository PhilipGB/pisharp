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

    private static SessionTurn Turn(string? parentId, string message)
    {
        using var json = JsonDocument.Parse("{\"state\":true}");
        return SessionTurn.Create(parentId, message, "assistant", json.RootElement);
    }
}
