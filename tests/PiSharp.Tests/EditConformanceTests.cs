using System.Text;
using PiSharp.Core;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

/// <summary>Cases observed at earendil-works/pi@002fc838 (edit-diff.ts, run via node).</summary>
public sealed class EditConformanceTests
{
    [Theory]
    [InlineData("one\r\ntwo\n", "one\ntwo", "three\nfour", "three\nfour\n")]
    [InlineData("a—b  \nnext\n", "a-b\nnext", "done", "done\n")]
    public void SingleReplacementMatchesPinnedEditPlanner(string content, string oldText, string newText, string expected) =>
        Assert.Equal(expected, FileEdits.Apply(content, [new TextEdit(oldText, newText)], "sample.txt"));

    [Fact]
    public void DisjointReplacementsUseOriginalSnapshot()
    {
        Assert.Equal("A B", FileEdits.Apply("alpha beta", [new("alpha", "A"), new("beta", "B")], "sample.txt"));
        Assert.Equal("a-B-C", FileEdits.Apply("a-b-c", [new("b", "B"), new("c", "C")], "sample.txt"));
    }

    [Fact]
    public void InvalidBatchIsAtomicAndErrorsMatchPinnedReference()
    {
        var path = "sample.txt";
        Assert.Equal("Found 2 occurrences of the text in sample.txt. The text must be unique. Please provide more context to make it unique.",
            Assert.Throws<ArgumentException>(() => FileEdits.Apply("a a", [new("a", "b")], path)).Message);
        Assert.Equal("edits[0] and edits[1] overlap in sample.txt. Merge them into one edit or target disjoint regions.",
            Assert.Throws<ArgumentException>(() => FileEdits.Apply("alpha beta", [new("alpha beta", "A"), new("beta", "B")], path)).Message);
        Assert.Throws<ArgumentException>(() => FileEdits.Apply("foo bar", [new("foo", "x"), new("missing", "y")], path));
    }

    [Fact]
    public async Task EditBatchPreservesBomLineEndingsAndDoesNotWriteOnValidationError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-edit-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "fixture.txt");
            var before = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n")).ToArray();
            await File.WriteAllBytesAsync(file, before);
            var tool = new CodingTools(dir);
            Assert.Contains("Could not find", (await Assert.ThrowsAsync<ToolFailureException>(() => tool.EditBatch("fixture.txt", [new("one", "ONE"), new("missing", "X")]))).Message);
            Assert.Equal(before, await File.ReadAllBytesAsync(file));
            Assert.Equal("Successfully replaced 2 block(s) in fixture.txt.",
                await tool.EditBatch("fixture.txt", [new("one", "ONE"), new("three", "THREE")]));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("ONE\r\ntwo\r\nTHREE\r\n")),
                await File.ReadAllBytesAsync(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentMutationsDoNotOverwriteEachOther()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-edit-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var tool = new CodingTools(dir);
            await tool.Write("fixture.txt", "one two");
            var first = tool.EditBatch("fixture.txt", [new("one", "ONE")]);
            var second = tool.EditBatch("fixture.txt", [new("two", "TWO")]);
            await Task.WhenAll(first, second);
            Assert.Equal("ONE TWO", await File.ReadAllTextAsync(Path.Combine(dir, "fixture.txt")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
