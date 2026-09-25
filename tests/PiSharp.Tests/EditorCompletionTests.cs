using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class EditorCompletionTests
{
    [Fact]
    public void SlashCompletionShowsAmbiguityAndCompletesUniqueName()
    {
        var completion = new EditorCompletion(Path.GetTempPath());
        var buffer = new EditorBuffer();
        buffer.SetText("/tr");
        Assert.Equal(["/tree", "/trust"], completion.Complete(buffer));
        Assert.Equal("/tr", buffer.Text);
        buffer.SetText("/rel");
        Assert.Equal(["/reload"], completion.Complete(buffer));
        Assert.Equal("/reload", buffer.Text);
        buffer.SetText("Please /rel");
        Assert.Empty(completion.Complete(buffer));
    }

    [Fact]
    public void PathCompletionIsRelativeAndHidesDotfilesByDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "data.txt"), "x");
            File.WriteAllText(Path.Combine(root, ".secret"), "x");
            var completion = new EditorCompletion(root);
            var buffer = new EditorBuffer();
            buffer.SetText("look @do");
            Assert.Equal(["@docs/"], completion.Complete(buffer));
            Assert.Equal("look @docs/", buffer.Text);
            buffer.SetText("@.");
            Assert.Equal(["@.secret"], completion.Complete(buffer));
            buffer.SetText("@");
            Assert.Equal(["@docs/", "@data.txt"], completion.Complete(buffer));
            Assert.DoesNotContain("@.secret", completion.Complete(buffer));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PathCompletionHandlesNestedUnicodePathsAndOpeningWrappers()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-unicode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "文档"));
        Directory.CreateDirectory(Path.Combine(root, "[slug]"));
        Directory.CreateDirectory(Path.Combine(root, "😀-one"));
        Directory.CreateDirectory(Path.Combine(root, "😀-two"));
        File.WriteAllText(Path.Combine(root, "src", "main.ts"), "x");
        File.WriteAllText(Path.Combine(root, "文档", "说明.md"), "x");
        File.WriteAllText(Path.Combine(root, "[slug]", "page.tsx"), "x");
        try
        {
            var completion = new EditorCompletion(root);
            var buffer = new EditorBuffer();
            buffer.SetText("See (`src/ma");
            Assert.Equal(["src/main.ts"], completion.Complete(buffer));
            Assert.Equal("See (`src/main.ts", buffer.Text);

            buffer.SetText("查看，@文档/说");
            Assert.Equal(["@文档/说明.md"], completion.Complete(buffer));
            Assert.Equal("查看，@文档/说明.md", buffer.Text);

            buffer.SetText("([slug]/pa");
            Assert.Equal(["[slug]/page.tsx"], completion.Complete(buffer));
            Assert.Equal("([slug]/page.tsx", buffer.Text);

            buffer.SetText("@😀");
            Assert.Equal(["@😀-one/", "@😀-two/"], completion.Complete(buffer));
            Assert.Equal("@😀-", buffer.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void QuotedDirectoryCompletionContinuesWithoutDuplicatingItsClosingQuote()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-complete-quoted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "my folder"));
        File.WriteAllText(Path.Combine(root, "my folder", "notes one.md"), "x");
        try
        {
            var completion = new EditorCompletion(root);
            var buffer = new EditorBuffer();
            buffer.SetText("@my");
            Assert.Equal(["@\"my folder/\""], completion.Complete(buffer));
            Assert.Equal("@\"my folder/\"", buffer.Text);
            Assert.Equal(buffer.Text.Length - 1, buffer.Cursor);

            Assert.Equal(["@\"my folder/notes one.md\""], completion.Complete(buffer));
            Assert.Equal("@\"my folder/notes one.md\"", buffer.Text);
            Assert.Equal(buffer.Text.Length - 1, buffer.Cursor);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
