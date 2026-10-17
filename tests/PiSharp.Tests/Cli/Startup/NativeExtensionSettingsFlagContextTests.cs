using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Tests;

public sealed class NativeExtensionSettingsFlagContextTests
{
    [Fact]
    public void SettingsAndFlagReadsUseCallTimeSnapshotsAndPreserveMissingVersusDefaultFalse()
    {
        var shellPrefix = "first";
        PiSharpExtensionFlagValue? configuredFlag = PiSharpExtensionFlagValue.FromBoolean(false);
        var context = new PiSharpExtensionSettingsFlagContext(
            () => JsonSerializer.SerializeToElement(new { shellCommandPrefix = shellPrefix }),
            name => name == "enabled" ? configuredFlag : null);

        Assert.Equal("first", context.GetSettings().GetProperty("shellCommandPrefix").GetString());
        Assert.False(context.GetFlag("enabled")!.BooleanValue);
        Assert.Null(context.GetFlag("missing"));

        shellPrefix = "refreshed";
        configuredFlag = PiSharpExtensionFlagValue.FromBoolean(true);
        Assert.Equal("refreshed", context.GetSettings().GetProperty("shellCommandPrefix").GetString());
        Assert.True(context.GetFlag("enabled")!.BooleanValue);
    }
}
