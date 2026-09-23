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
            Assert.DoesNotContain("@.secret", completion.Complete(buffer));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
