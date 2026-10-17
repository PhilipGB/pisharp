using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class ExtensionShortcutRegistrationTests
{
    [Fact]
    public void FailedConfigurationRestoresShortcutRegistrations()
    {
        var registration = new ExtensionRegistration();
        var restore = registration.CreateConfigurationCheckpoint();
        registration.SetCurrentSourceInfo(Source("/extensions/partial.dll"));
        registration.AddShortcut(PiSharpShortcutKey.Parse("ctrl+shift+x"), Handler, "temporary");

        restore();

        Assert.Empty(registration.ExtensionShortcuts);
    }

    [Fact]
    public void ShortcutOwnershipTracksExtensionReplacementAndRemoval()
    {
        var registration = new ExtensionRegistration();
        var original = Source("/extensions/native.dll");
        var replacement = original with { Source = "native-package-v2" };
        PiSharpExtensionShortcutHandler expectedHandler = ReplacementHandler;
        registration.SetCurrentSourceInfo(original);
        registration.AddShortcut(PiSharpShortcutKey.Parse("ctrl+shift+x"), Handler, "first");
        registration.AddShortcut(PiSharpShortcutKey.Parse("ctrl+shift+x"), expectedHandler, "second");

        Assert.Same(expectedHandler, Assert.Single(registration.ExtensionShortcuts).Handler);
        Assert.Equal(original, registration.ExtensionShortcuts[0].SourceInfo);

        registration.ReconcileExtension(original, replacement);
        Assert.Equal(replacement, Assert.Single(registration.ExtensionShortcuts).SourceInfo);

        registration.ReconcileExtension(replacement, selected: null);
        Assert.Empty(registration.ExtensionShortcuts);
    }

    [Fact]
    public void ShortcutRegistrationsFollowSelectedExtensionLoadOrder()
    {
        var registration = new ExtensionRegistration();
        var first = Source("/extensions/first.dll");
        var second = Source("/extensions/second.dll");
        registration.SetCurrentSourceInfo(first);
        registration.AddShortcut(PiSharpShortcutKey.Parse("ctrl+shift+x"), Handler);
        registration.SetCurrentSourceInfo(second);
        registration.AddShortcut(PiSharpShortcutKey.Parse("ctrl+shift+x"), ReplacementHandler);

        registration.OrderExtensions([second, first]);

        Assert.Equal(["/extensions/second.dll", "/extensions/first.dll"],
            registration.ExtensionShortcuts.Select(shortcut => shortcut.SourceInfo.Path));
    }

    private static ResourceSourceInfo Source(string path) =>
        new(path, "native-package", "user", "package", "/extensions");

    private static Task Handler(PiSharpExtensionCommandContext _, CancellationToken __) => Task.CompletedTask;

    private static Task ReplacementHandler(PiSharpExtensionCommandContext _, CancellationToken __) => Task.CompletedTask;
}
