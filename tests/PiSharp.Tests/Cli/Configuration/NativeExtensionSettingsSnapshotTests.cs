using PiSharp.Cli;
using PiSharp.Cli.Configuration;

namespace PiSharp.Tests;

public sealed class NativeExtensionSettingsSnapshotTests
{
    [Fact]
    public void SnapshotUsesDetachedLowerCamelCaseEffectiveSettings()
    {
        var global = new UserSettings(DefaultModel: "global/model", ShellCommandPrefix: "global-prefix");
        var project = new UserSettings(DefaultModel: "project/model", ShellCommandPrefix: "project-prefix");

        var snapshot = NativeExtensionSettingsSnapshot.Create(global.Overlay(project));

        Assert.Equal("project/model", snapshot.GetProperty("defaultModel").GetString());
        Assert.Equal("project-prefix", snapshot.GetProperty("shellCommandPrefix").GetString());
        Assert.False(snapshot.TryGetProperty("DefaultModel", out _));
        Assert.False(snapshot.TryGetProperty("terminalTrueColorOverride", out _));
        Assert.False(snapshot.TryGetProperty("skillCommandsEnabled", out _));
        Assert.False(snapshot.TryGetProperty("shareDestination", out _));
    }
}
