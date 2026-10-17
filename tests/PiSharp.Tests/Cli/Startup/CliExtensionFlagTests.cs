using PiSharp.Cli;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Tests;

public sealed class CliExtensionFlagTests
{
    private static readonly ResourceSourceInfo s_source = new("/extensions/fixture.dll", "native", "project",
        "explicit", null);

    [Fact]
    public void ExtensionFlagsAreParsedAfterRegistrationAndPreserveTheirDeclaredValueTypes()
    {
        var flags = new[]
        {
            new RegisteredExtensionFlag("plan", null, PiSharpExtensionFlagType.Boolean,
                PiSharpExtensionFlagValue.FromBoolean(false), s_source),
            new RegisteredExtensionFlag("target", "Deployment target", PiSharpExtensionFlagType.String,
                PiSharpExtensionFlagValue.FromString("staging"), s_source)
        };

        var discovery = CliArguments.ParseForExtensionDiscovery(["--mode", "rpc", "--target", "production"]);
        Assert.Equal("rpc", discovery.Mode);

        var parsed = CliArguments.ParseWithExtensionFlags(
            ["--plan", "--target", "production", "--print", "hello"], flags);

        Assert.True(parsed.ExtensionFlagValues["plan"].BooleanValue);
        Assert.Equal("production", parsed.ExtensionFlagValues["target"].StringValue);
        Assert.Equal("hello", parsed.Prompt);

        var promptAfterBoolean = CliArguments.ParseWithExtensionFlags(
            ["--plan", "describe", "--target", "production"], flags);
        Assert.True(promptAfterBoolean.ExtensionFlagValues["plan"].BooleanValue);
        Assert.Equal("describe", promptAfterBoolean.Prompt);

        var falseOverride = CliArguments.ParseWithExtensionFlags(["--plan=false"], flags);
        Assert.False(falseOverride.ExtensionFlagValues["plan"].BooleanValue);
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["--plan"]));
    }

    [Fact]
    public void StringFlagsRequireValuesAndRegistrationsCannotShadowCoreOptions()
    {
        var stringFlag = new RegisteredExtensionFlag("target", null, PiSharpExtensionFlagType.String, null, s_source);
        Assert.Contains("requires a value", Assert.Throws<ArgumentException>(() =>
            CliArguments.ParseWithExtensionFlags(["--target"], [stringFlag])).Message);

        var conflictingFlag = stringFlag with { Name = "model" };
        var conflict = Assert.Throws<ArgumentException>(() =>
            CliArguments.ParseWithExtensionFlags([], [conflictingFlag]));
        Assert.Contains("--model", conflict.Message);
        Assert.Contains(s_source.Path, conflict.Message);
    }
}
