using PiSharp.Cli;

namespace PiSharp.Core.Tests;

public sealed class FileArgumentLoaderTests
{
    [Fact]
    public async Task BuildsPromptWithEscapedFileEnvelope()
    {
        using var temp = TempDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "notes&.txt"), "alpha & <beta>");

        var prompt = await FileArgumentLoader.BuildPromptAsync(
            "Summarize this",
            ["notes&.txt"],
            temp.Path,
            CancellationToken.None);

        Assert.Contains("<file name=\"", prompt, StringComparison.Ordinal);
        Assert.Contains("alpha & <beta>", prompt, StringComparison.Ordinal);
        Assert.Contains("&amp;", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Summarize this", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadsImageArgumentsAsBinaryContent()
    {
        using var temp = TempDirectory.Create();
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "diagram.png"), [137, 80, 78, 71]);

        var prompt = await FileArgumentLoader.LoadAsync(
            "Describe it",
            ["diagram.png"],
            temp.Path,
            CancellationToken.None);

        Assert.Single(prompt.Images);
        Assert.Contains("diagram.png", prompt.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMissingFileArguments()
    {
        using var temp = TempDirectory.Create();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            FileArgumentLoader.BuildPromptAsync(null, ["missing.txt"], temp.Path, CancellationToken.None));
    }
}
