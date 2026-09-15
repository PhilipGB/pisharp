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
    public async Task RejectsMissingFileArguments()
    {
        using var temp = TempDirectory.Create();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            FileArgumentLoader.BuildPromptAsync(null, ["missing.txt"], temp.Path, CancellationToken.None));
    }
}
