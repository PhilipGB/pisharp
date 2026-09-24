using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class CliPromptOverridesTests
{
    [Fact]
    public async Task LiteralAndFileOverridesReplaceDiscoveredResourcesAndReloadFromDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cli-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "SYSTEM.txt");
            await File.WriteAllTextAsync(file, "\uFEFFFROM FILE");
            var cli = CliArguments.Parse(["--system-prompt", "SYSTEM.txt", "--append-system-prompt", "literal one",
                "--append-system-prompt", "SYSTEM.txt"]);
            var resolved = await CliPromptOverrides.ResolveAsync(cli, ("discovered", "discovered append"), root);
            Assert.Equal("FROM FILE", resolved.System);
            Assert.Equal("literal one\n\nFROM FILE", resolved.Append);
            await File.WriteAllTextAsync(file, "UPDATED");
            Assert.Equal("UPDATED", (await CliPromptOverrides.ResolveAsync(cli, (null, null), root)).System);
            var literal = CliArguments.Parse(["--system-prompt", "a literal prompt\nwith lines"]);
            Assert.Equal("a literal prompt\nwith lines", (await CliPromptOverrides.ResolveAsync(literal, ("discovered", "append"), root)).System);
            Assert.Equal("append", (await CliPromptOverrides.ResolveAsync(literal, ("discovered", "append"), root)).Append);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OversizedOverrideFailsInsteadOfSilentlyTruncating()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-cli-prompt-large-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "large.txt"), new string('X', 65537));
            await Assert.ThrowsAsync<InvalidDataException>(() => CliPromptOverrides.ResolveAsync(
                CliArguments.Parse(["--system-prompt", "large.txt"]), (null, null), root));
            Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--append-system-prompt"]));
        }
        finally { Directory.Delete(root, true); }
    }
}
