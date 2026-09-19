using PiSharp.Core.Settings;

namespace PiSharp.Core.Tests;

/// <summary>
/// Conformance tests for Pi keybinding resolution: user file loading, legacy name
/// migration, malformed-file tolerance, and the Linux default tables from the
/// pinned keybindings.ts.
/// </summary>
public class KeybindingsTests
{
    [Fact]
    public void LinuxDefaultsMatchPiTables()
    {
        var manager = KeybindingsManager.Create();

        Assert.Equal(["escape"], manager.GetKeys("app.interrupt"));
        Assert.Equal(["alt+enter"], manager.GetKeys("app.message.followUp"));
        Assert.Equal(["alt+up"], manager.GetKeys("app.message.dequeue"));
        Assert.Equal(["shift+tab"], manager.GetKeys("app.thinking.cycle"));
        Assert.Equal(["ctrl+p"], manager.GetKeys("app.model.cycleForward"));
        Assert.Equal(["shift+ctrl+p"], manager.GetKeys("app.model.cycleBackward"));
        Assert.Equal(["ctrl+-"], manager.GetKeys("tui.editor.undo"));
        Assert.Equal(["ctrl+shift+up", "ctrl+up"], manager.GetKeys("tui.altScreen.previousPrompt"));
        Assert.Equal(["ctrl+shift+down", "ctrl+down"], manager.GetKeys("tui.altScreen.nextPrompt"));
        Assert.Equal(["ctrl+shift+f"], manager.GetKeys("tui.altScreen.search"));
        Assert.Equal(["ctrl+z"], manager.GetKeys("app.suspend"));
        Assert.Equal(["ctrl+left", "alt+left"], manager.GetKeys("app.tree.foldOrUp"));
        Assert.Equal(["ctrl+right", "alt+right"], manager.GetKeys("app.tree.unfoldOrDown"));
        Assert.Equal(["enter"], manager.GetKeys("tui.input.submit"));
    }

    [Fact]
    public void UserOverridesWinOverDefaults()
    {
        var manager = KeybindingsManager.Create(
            new Dictionary<string, KeybindingValue>
            {
                ["app.message.followUp"] = KeybindingValue.Of("ctrl+enter"),
            });

        Assert.Equal(["ctrl+enter"], manager.GetKeys("app.message.followUp"));
        Assert.Equal(["escape"], manager.GetKeys("app.interrupt"));
    }

    [Fact]
    public void LegacyNamesMigrateToCurrentNames()
    {
        var migrated = KeybindingsManager.MigrateLegacyNames(new Dictionary<string, object?>
        {
            ["submit"] = "ctrl+enter",
            ["followUp"] = "alt+enter",
            ["customBinding"] = "f13",
        });

        Assert.Equal("ctrl+enter", migrated["tui.input.submit"]);
        Assert.Equal("alt+enter", migrated["app.message.followUp"]);
        Assert.Equal("f13", migrated["customBinding"]);
        Assert.DoesNotContain("submit", migrated.Keys);
        Assert.DoesNotContain("followUp", migrated.Keys);
    }

    [Fact]
    public void CurrentNameWinsOverLegacyNameWhenBothPresent()
    {
        var migrated = KeybindingsManager.MigrateLegacyNames(new Dictionary<string, object?>
        {
            ["submit"] = "legacy-key",
            ["tui.input.submit"] = "current-key",
        });

        Assert.Equal("current-key", migrated["tui.input.submit"]);
        Assert.DoesNotContain("legacy-key", migrated.Values);
    }

    [Fact]
    public async Task MalformedKeybindingFilesYieldEmptyConfiguration()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "keybindings.json");
        File.WriteAllText(path, "{ not json");

        var manager = KeybindingsManager.Create(await LoadFromFile(path));

        Assert.Equal(["escape"], manager.GetKeys("app.interrupt"));
    }

    [Fact]
    public async Task MissingKeybindingFileYieldEmptyConfiguration()
    {
        using var temp = TempDirectory.Create();
        var config = await LoadFromFile(Path.Combine(temp.Path, "absent.json"));
        Assert.Empty(config);
    }

    [Fact]
    public async Task FileWithMixedValueShapesLoadsStringsAndArrays()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "keybindings.json");
        File.WriteAllText(path, """{"app.interrupt":"f9","tui.editor.cursorUp":["up","k"]}""");

        var config = await LoadFromFile(path);
        Assert.Equal(["f9"], config["app.interrupt"].Keys);
        Assert.Equal(["up", "k"], config["tui.editor.cursorUp"].Keys);
    }

    [Fact]
    public void EffectiveConfigListsDefaultsFirstThenSortedUnknowns()
    {
        var manager = KeybindingsManager.Create(
            new Dictionary<string, KeybindingValue>
            {
                ["zeta.custom"] = KeybindingValue.Of("f2"),
                ["alpha.custom"] = KeybindingValue.Of("f3"),
                ["app.interrupt"] = KeybindingValue.Of("f9"),
            });

        var effective = manager.GetEffectiveConfig();
        Assert.Equal("f9", effective["app.interrupt"].Primary);
        Assert.Equal("f2", effective["zeta.custom"].Primary);
        Assert.Equal("f3", effective["alpha.custom"].Primary);

        var names = manager.KeybindingNames.ToList();
        Assert.True(names.IndexOf("app.interrupt") < names.IndexOf("alpha.custom"));
        Assert.True(names.IndexOf("alpha.custom") < names.IndexOf("zeta.custom"));
    }

    private static Task<IReadOnlyDictionary<string, KeybindingValue>> LoadFromFile(string path) =>
        KeybindingsManager.LoadConfigFileAsync(path, CancellationToken.None);
}
