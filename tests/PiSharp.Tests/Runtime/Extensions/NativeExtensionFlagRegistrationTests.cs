using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class NativeExtensionFlagRegistrationTests
{
    [Fact]
    public void RegistrationValidatesTypesAndTracksTheDeclaringExtension()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var firstSource = Source("/extensions/first.dll");
        registration.SetCurrentSourceInfo(firstSource);

        adapter.RegisterFlag("plan", PiSharpExtensionFlagType.Boolean, "Create a plan",
            PiSharpExtensionFlagValue.FromBoolean(false));
        Assert.Throws<ArgumentException>(() => adapter.RegisterFlag("bad-default", PiSharpExtensionFlagType.String,
            defaultValue: PiSharpExtensionFlagValue.FromBoolean(true)));

        var flag = Assert.Single(registration.ExtensionFlags);
        Assert.Equal("plan", flag.Name);
        Assert.Equal(firstSource, flag.SourceInfo);
        Assert.False(flag.DefaultValue!.BooleanValue);

        var secondSource = Source("/extensions/second.dll");
        registration.SetCurrentSourceInfo(secondSource);
        var conflict = Assert.Throws<InvalidOperationException>(() =>
            adapter.RegisterFlag("plan", PiSharpExtensionFlagType.Boolean));
        Assert.Contains(firstSource.Path, conflict.Message);
        Assert.Contains(secondSource.Path, conflict.Message);

        registration.SetCurrentSourceInfo(null);
        Assert.Throws<InvalidOperationException>(() => adapter.RegisterFlag("unowned", PiSharpExtensionFlagType.String));
    }

    [Fact]
    public void RegistrationCheckpointRestoresExtensionFlagDefinitions()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        registration.SetCurrentSourceInfo(Source("/extensions/fixture.dll"));
        adapter.RegisterFlag("stable", PiSharpExtensionFlagType.String, defaultValue:
            PiSharpExtensionFlagValue.FromString("initial"));

        var restore = registration.CreateConfigurationCheckpoint();
        adapter.RegisterFlag("temporary", PiSharpExtensionFlagType.Boolean);
        restore();

        Assert.Equal("stable", Assert.Single(registration.ExtensionFlags).Name);
    }

    private static ResourceSourceInfo Source(string path) => new(path, "native", "project", "explicit", null);
}
