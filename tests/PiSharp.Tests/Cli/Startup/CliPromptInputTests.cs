using System.Text;
using PiSharp.Cli;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class CliPromptInputTests
{
    [Fact]
    public async Task ExplicitEmptyAppendListSuppressesPreviouslyDiscoveredResources()
    {
        var result = await CliPromptOverrides.ResolveWithSourcesAsync(CliArguments.Parse([]) with { AppendSystemPrompts = [] },
            new("system", "append", null, ["previous.txt"]), Path.GetTempPath());
        Assert.Equal("", result.Append);
        Assert.Empty(result.AppendPaths);
        Assert.Equal("system", result.System);
    }

    [Theory]
    [InlineData("literal prompt")]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData("--literal")]
    public async Task PromptValuesAreConsumedAsValuesIncludingEmptyWhitespaceAndDashPrefixes(string value)
    {
        var cli = CliArguments.Parse(["--system-prompt", value]);
        Assert.Equal(value, cli.SystemPrompt);
        var result = await CliPromptOverrides.ResolveWithSourcesAsync(cli, new("discovered", "append", null, []), Path.GetTempPath());
        Assert.Equal(value.Length == 0 ? null : value, result.System);
        Assert.Equal("append", result.Append);
    }

    [Fact]
    public async Task EmptyAppendLiteralIsIgnoredWhileAnEmptyFileRemainsASection()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "empty.txt"), "");
            var ignored = await CliPromptOverrides.ResolveAsync(CliArguments.Parse(["--append-system-prompt", "", "--append-system-prompt", "literal"]),
                (null, "discovered"), root);
            Assert.Equal("literal", ignored.Append);
            var retained = await CliPromptOverrides.ResolveWithSourcesAsync(CliArguments.Parse(["--append-system-prompt", "empty.txt", "--append-system-prompt", "literal"]),
                new(null, "discovered", null, []), root);
            Assert.Equal("\n\nliteral", retained.Append);
            Assert.Equal([Path.Combine(root, "empty.txt")], retained.AppendPaths);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FileInputUsesSingleBomRemovalAndUtf8Replacement()
    {
        var root = CreateDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "prompt.txt"), Encoding.UTF8.GetBytes("\uFEFF\uFEFFretained"));
            var result = await CliPromptOverrides.ResolveAsync(CliArguments.Parse(["--system-prompt", "prompt.txt"]), (null, null), root);
            Assert.Equal("\uFEFFretained", result.System);
            await File.WriteAllBytesAsync(Path.Combine(root, "prompt.txt"), [98, 97, 100, 32, 255]);
            result = await CliPromptOverrides.ResolveAsync(CliArguments.Parse(["--system-prompt", "prompt.txt"]), (null, null), root);
            Assert.Equal("bad \uFFFD", result.System);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cli-prompt-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
