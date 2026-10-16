using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class InteractiveCommandCatalogTests
{
    private static readonly string[] s_expectedBuiltIns =
    [
        "/tree", "/branch", "/fork", "/clone", "/new", "/sessions", "/resume", "/delete-session",
        "/import", "/name", "/model", "/models", "/scoped-models", "/thinking", "/login", "/logout",
        "/llama", "/settings", "/compact", "/copy", "/export", "/share", "/export-jsonl", "/session",
        "/trust", "/reload", "/hotkeys", "/mcp", "/quit", "/exit"
    ];

    [Fact]
    public void BuiltInMetadataHasStableCoverageAndUsefulDescriptions()
    {
        var commands = InteractiveCommandCatalog.BuiltIns;

        Assert.Equal(s_expectedBuiltIns, commands.Select(command => command.Command));
        Assert.All(commands, command => Assert.False(string.IsNullOrWhiteSpace(command.Description)));
        Assert.Equal(commands.Select(command => command.Command).Distinct(StringComparer.OrdinalIgnoreCase),
            commands.Select(command => command.Command));
        Assert.Equal("Select the active model's thinking level.", InteractiveCommandCatalog.Describe("/thinking"));
    }

    [Fact]
    public void DynamicCommandsAppendInOrderAndDuplicatesAreRemovedCaseInsensitively()
    {
        var dynamicCommands = new[]
        {
            new InteractiveCommandMetadata("/prompt-build", "Run a prompt command."),
            new InteractiveCommandMetadata("/skill-review", "Run a skill command."),
            new InteractiveCommandMetadata("/THINKING", "Duplicate built-in."),
            new InteractiveCommandMetadata("/prompt-build", "Duplicate dynamic command.")
        };

        var merged = InteractiveCommandCatalog.Merge(dynamicCommands);
        var names = merged.Select(command => command.Command).ToArray();

        Assert.Equal(s_expectedBuiltIns.Concat(["/prompt-build", "/skill-review"]), names);
        Assert.Equal(names.Distinct(StringComparer.OrdinalIgnoreCase), names);
        Assert.Equal("Select the active model's thinking level.",
            merged.Single(command => command.Command == "/thinking").Description);
        Assert.Equal(merged.Select(command => command.Command),
            InteractiveCommandCatalog.Merge(dynamicCommands).Select(command => command.Command));
    }
}
