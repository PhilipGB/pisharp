using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class DefaultToolReloadPolicyTests
{
    [Theory]
    [InlineData("read,+write", "tool names cannot be mixed")]
    [InlineData("+mcp__*", "take exact tool names")]
    public void RejectsAmbiguousOrPatternToolModifiers(string tools, string message)
    {
        var error = Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--tools", tools]));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void OrderedModifiersSurviveStartupAndSettingsReload()
    {
        var arguments = DefaultToolReloadPolicy.ApplyStartupDefaults(
            CliArguments.Parse(["--tools", "-grep,+write,-write,+write"]),
            new UserSettings(DefaultTools: ["read", "grep"]), _ => null, false);
        Assert.True(DefaultToolReloadPolicy.UsesSettingsDefaults(arguments));
        Assert.Equal(["read", "write"], arguments.Tools);
        var reload = DefaultToolReloadPolicy.Resolve(arguments, true,
            new UserSettings(DefaultTools: ["read"]), new UserSettings(DefaultTools: ["read", "grep", "ls"]),
            ["read", "write"], _ => null);
        Assert.Equal(["read", "write", "ls"], reload.ActiveToolNames);
        Assert.Equal(["read", "ls", "write"], reload.Arguments.Tools);
    }

    [Fact]
    public void ReloadAddsNewDefaultsWithoutRemovingActiveToolsOrReenablingDisabledOnes()
    {
        var previousSettings = new UserSettings(DefaultTools: ["read", "bash", "edit", "write"]);
        var nextSettings = new UserSettings(DefaultTools: ["read", "bash", "+grep"]);
        var cli = CliArguments.Parse([]);

        var activeToolNames = DefaultToolReloadPolicy.Resolve(cli, usesSettingsDefaults: true,
            previousSettings, nextSettings, ["read", "write"], _ => null);

        Assert.Equal(["read", "bash", "grep"], activeToolNames.Arguments.Tools);
        Assert.Equal(["read", "write", "grep"], activeToolNames.ActiveToolNames);
    }

    [Fact]
    public void SettingsReloadReplacesInheritedModelScopeButKeepsExplicitCliScope()
    {
        var original = new UserSettings(EnabledModels: ["fixture/first"]);
        var updated = new UserSettings(EnabledModels: ["fixture/second"]);
        var inherited = DefaultToolReloadPolicy.ApplyStartupDefaults(CliArguments.Parse([]), original,
            _ => null, preserveSessionModel: false);
        var inheritedReload = DefaultToolReloadPolicy.Resolve(inherited, usesSettingsDefaults: true,
            original, updated, [], _ => null);

        var explicitScope = DefaultToolReloadPolicy.ApplyStartupDefaults(
            CliArguments.Parse(["--models", "fixture/first"]), original, _ => null, preserveSessionModel: false);
        var explicitReload = DefaultToolReloadPolicy.Resolve(explicitScope, usesSettingsDefaults: true,
            original, updated, [], _ => null);

        Assert.Equal(["fixture/second"], inheritedReload.Arguments.ScopedModels);
        Assert.Equal(["fixture/first"], explicitReload.Arguments.ScopedModels);
    }

    [Fact]
    public void NoBuiltinToolsContinuesToFilterBuiltinsAddedBySettings()
    {
        var previousSettings = new UserSettings(DefaultTools: []);
        var nextSettings = new UserSettings(DefaultTools: ["+grep", "+custom"]);
        var cli = CliArguments.Parse(["--no-builtin-tools"]);

        var startup = DefaultToolReloadPolicy.ApplyStartupDefaults(cli, nextSettings, _ => null,
            preserveSessionModel: false);
        var reload = DefaultToolReloadPolicy.Resolve(startup, usesSettingsDefaults: true,
            previousSettings, nextSettings, [], _ => null);

        Assert.Equal(["custom"], startup.Tools);
        Assert.Equal(["custom"], reload.Arguments.Tools);
        Assert.Equal(["custom"], reload.ActiveToolNames);
    }

    [Fact]
    public void ExcludedDefaultsStayOutOfTheReloadedLoadout()
    {
        var previousSettings = new UserSettings(DefaultTools: ["read", "grep"]);
        var nextSettings = new UserSettings(DefaultTools: ["read", "+grep", "+write"]);
        var cli = CliArguments.Parse(["--exclude-tools", "grep"]);

        var plan = DefaultToolReloadPolicy.Resolve(cli, usesSettingsDefaults: true,
            previousSettings, nextSettings, ["read", "grep"], _ => null);

        Assert.Equal(["read", "grep", "write"], plan.Arguments.Tools);
        Assert.Equal(["read", "write"], plan.ActiveToolNames);
    }

    [Theory]
    [InlineData("--tools", "read")]
    [InlineData("--no-tools", "")]
    public void ExplicitToolModesDoNotAdoptReloadedDefaults(string flag, string value)
    {
        var cli = value.Length == 0 ? CliArguments.Parse([flag]) : CliArguments.Parse([flag, value]);
        var plan = DefaultToolReloadPolicy.Resolve(cli, usesSettingsDefaults: false,
            new UserSettings(), new UserSettings(DefaultTools: ["+grep"]), ["read"], _ => null);

        Assert.Equal(cli, plan.Arguments);
        Assert.Null(plan.ActiveToolNames);
    }
}
