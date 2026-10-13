using PiSharp.Cli;

namespace PiSharp.Tests;

public sealed class DefaultToolReloadPolicyTests
{
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
