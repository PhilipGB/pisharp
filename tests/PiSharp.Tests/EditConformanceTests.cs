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
    [InlineData("hello\uFEFF\n", "hello\n", "changed\n", "changed\n")]
    public void SingleReplacementMatchesPinnedEditPlanner(string content, string oldText, string newText, string expected) =>
        Assert.Equal(expected, FileEdits.Apply(content, [new TextEdit(oldText, newText)], "sample.txt"));

    [Fact]
    public void FuzzyMatchingDoesNotTrimWhitespaceOutsideTheJavaScriptSet()
    {
        var error = Assert.Throws<ArgumentException>(() => FileEdits.Apply("hello\u0085\n", [new("hello\n", "changed\n")], "sample.txt"));
        Assert.Equal("Could not find the exact text in sample.txt. The old text must match exactly including all whitespace and newlines.", error.Message);
    }

    [Fact]
    public void DisjointReplacementsUseOriginalSnapshot()
    {
        Assert.Equal("A B", FileEdits.Apply("alpha beta", [new("alpha", "A"), new("beta", "B")], "sample.txt"));
        Assert.Equal("a-B-C", FileEdits.Apply("a-b-c", [new("b", "B"), new("c", "C")], "sample.txt"));
    }

    [Fact]
    public void EditDiffAndPatchMatchPinnedSingleLineFormat()
    {
        var plan = FileEdits.Plan("Hello, world!", [new("world", "testing")], "sample.txt");
        var details = FileEditDiff.Create("sample.txt", plan.OriginalContent, plan.NewContent);

        Assert.Equal("-1 Hello, world!\n+1 Hello, testing!", details.Diff);
        Assert.Equal("--- sample.txt\n+++ sample.txt\n@@ -1,1 +1,1 @@\n-Hello, world!\n\\ No newline at end of file\n+Hello, testing!\n\\ No newline at end of file\n", details.Patch);
        Assert.Equal(1, details.FirstChangedLine);
    }

    [Fact]
    public void EditDiffLineNumbersAndContextMatchPinnedMultipleEdits()
    {
        const string source = "alpha\nbeta\ngamma\ndelta\n";
        var plan = FileEdits.Plan(source,
            [new("alpha\n", "ALPHA\n"), new("gamma\n", "GAMMA\n")], "sample.txt");
        var details = FileEditDiff.Create("sample.txt", plan.OriginalContent, plan.NewContent);

        Assert.Equal("-1 alpha\n+1 ALPHA\n 2 beta\n-3 gamma\n+3 GAMMA\n 4 delta", details.Diff);
        Assert.Equal(1, details.FirstChangedLine);
    }

    [Fact]
    public void EditDiffAndPatchMatchPinnedSeparatedHunks()
    {
        var original = string.Join('\n', Enumerable.Range(1, 15).Select(line => $"line {line:00}")) + "\n";
        var plan = FileEdits.Plan(original,
            [new("line 02\n", "LINE 02\n"), new("line 15\n", "LINE 15\n")], "sample.txt");
        var details = FileEditDiff.Create("sample.txt", plan.OriginalContent, plan.NewContent);

        Assert.Equal("  1 line 01\n- 2 line 02\n+ 2 LINE 02\n  3 line 03\n  4 line 04\n  5 line 05\n  6 line 06\n    ...\n 11 line 11\n 12 line 12\n 13 line 13\n 14 line 14\n-15 line 15\n+15 LINE 15", details.Diff);
        Assert.Equal("--- sample.txt\n+++ sample.txt\n@@ -1,6 +1,6 @@\n line 01\n-line 02\n+LINE 02\n line 03\n line 04\n line 05\n line 06\n@@ -11,5 +11,5 @@\n line 11\n line 12\n line 13\n line 14\n-line 15\n+LINE 15\n", details.Patch);
        Assert.Equal(2, details.FirstChangedLine);
    }

    [Theory]
    [InlineData("alpha\nbeta\n", "NEW\nalpha\nbeta\n", "+1 NEW\n 1 alpha\n 2 beta", "--- sample.txt\n+++ sample.txt\n@@ -1,2 +1,3 @@\n+NEW\n alpha\n beta\n", 1)]
    [InlineData("alpha\nbeta\n", "beta\n", "-1 alpha\n 2 beta", "--- sample.txt\n+++ sample.txt\n@@ -1,2 +1,1 @@\n-alpha\n beta\n", 1)]
    [InlineData("a\nb\n", "b\na\n", "-1 a\n 2 b\n+2 a", "--- sample.txt\n+++ sample.txt\n@@ -1,2 +1,2 @@\n-a\n b\n+a\n", 1)]
    [InlineData("x\nx\ny\n", "x\ny\nx\n", " 1 x\n-2 x\n 3 y\n+3 x", "--- sample.txt\n+++ sample.txt\n@@ -1,3 +1,3 @@\n x\n-x\n y\n+x\n", 2)]
    [InlineData("", "new\n", "+1 new", "--- sample.txt\n+++ sample.txt\n@@ -0,0 +1,1 @@\n+new\n", 1)]
    [InlineData("old\n", "", "-1 old", "--- sample.txt\n+++ sample.txt\n@@ -1,1 +0,0 @@\n-old\n", 1)]
    public void EditDiffAndPatchMatchPinnedLeadingInsertionsAndDeletions(string original, string updated,
        string expectedDiff, string expectedPatch, int expectedFirstChangedLine)
    {
        var details = FileEditDiff.Create("sample.txt", original, updated);

        Assert.Equal(expectedDiff, details.Diff);
        Assert.Equal(expectedPatch, details.Patch);
        Assert.Equal(expectedFirstChangedLine, details.FirstChangedLine);
    }

    [Fact]
    public void EditDiffCollapsesLargeUnchangedGaps()
    {
        var lines = Enumerable.Range(1, 600).Select(line => $"line {line:000}");
        var original = string.Join('\n', lines) + "\n";
        var plan = FileEdits.Plan(original,
        [
            new("line 100\n", "LINE 100\n"),
            new("line 300\n", "LINE 300\n"),
            new("line 500\n", "LINE 500\n")
        ], "large.txt");
        var diff = FileEditDiff.Create("large.txt", plan.OriginalContent, plan.NewContent);

        Assert.Contains("LINE 100", diff.Diff);
        Assert.Contains("LINE 300", diff.Diff);
        Assert.Contains("LINE 500", diff.Diff);
        Assert.Contains("...", diff.Diff);
        Assert.DoesNotContain("line 250", diff.Diff);
        Assert.True(diff.Diff.Split('\n').Length < 50);
        Assert.Equal(100, diff.FirstChangedLine);
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
    public async Task MissingEditTargetUsesPinnedNoSuchFileErrorWithoutCreatingIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pisharp-edit-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var tool = new CodingTools(dir);
            var error = await Assert.ThrowsAsync<ToolFailureException>(() => tool.Edit("missing.txt", "before", "after"));
            Assert.Equal("Could not edit file: missing.txt. Error code: ENOENT.", error.Message);
            Assert.False(File.Exists(Path.Combine(dir, "missing.txt")));
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
