using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalSettingsPickerTests
{
    [Fact]
    public void SettingsKeepPiSharedOrderAndAppendPiSharpOptions()
    {
        // Pi source receipt: settings-selector.ts, main, inspected 2026-10-16
        // https://github.com/earendil-works/pi/blob/main/packages/coding-agent/src/modes/interactive/components/settings-selector.ts
        // The prompt-specified first ten are authoritative; the remaining supported shared entries
        // retain upstream order. Cache miss notices and changelog collapsing are
        // out of scope because their cache diagnostics and update notification owners are absent.
        var ids = TerminalSettingsPicker.Settings.Select(setting => setting.Id).ToArray();

        Assert.Equal(new[]
        {
            "compaction.enabled", "images.autoResize", "images.blockImages", "enableSkillCommands",
            "showHardwareCursor", "editorPaddingX", "outputPad", "autocompleteMaxVisible",
            "terminal.clearOnShrink", "terminal.showTerminalProgress"
        }, ids.Take(10));
        Assert.Equal(new[]
        {
            "steeringMode", "followUpMode", "sessionShareDestination", "httpIdleTimeoutMs",
            "hideThinkingBlock", "markdown.mermaid", "quietStartup", "defaultProjectTrust",
            "doubleEscapeAction", "treeFilterMode",
            "modelThinkingLevels", "theme", "fullscreenWheelScrollLines"
        }, ids.Skip(10).Take(13));
        Assert.Equal(new[]
        {
            "defaultThinkingLevel", "httpProxy", "terminal.trueColor", "externalEditor",
            "retry.provider.timeoutMs", "retry.provider.maxRetries",
            "retry.provider.maxRetryDelayMs", "markdown.codeBlockIndent"
        }, ids.Skip(23));
        Assert.DoesNotContain(ids, id => id is "showCacheMissNotices" or "collapseChangelog");
    }

    [Theory]
    [InlineData(44)]
    [InlineData(80)]
    public void SettingsListKeepsAlignedColumnsAndBoundedWindowAtDifferentWidths(int width)
    {
        var items = TerminalSettingsPicker.Settings.Select(setting =>
            new TerminalSettingsListItem(setting.Id, setting.Label, "enabled", setting.Description,
                setting.Id + " " + setting.Label)).ToArray();
        var list = new TerminalSettingsList("Settings", items, "compaction.enabled", "User settings",
            canChangeScope: true);

        var rendered = list.Render(width, 24, TerminalTheme.Default);
        var plainRows = rendered.Select(TerminalTextLayout.StripFormatting).ToArray();
        var settingRows = plainRows.Where(row => row.StartsWith("> ", StringComparison.Ordinal) ||
            row.StartsWith("  ", StringComparison.Ordinal)).ToArray();
        var firstValue = settingRows[0].IndexOf("enabled", StringComparison.Ordinal);
        var secondValue = settingRows[1].IndexOf("enabled", StringComparison.Ordinal);

        Assert.Equal(10, settingRows.Length);
        Assert.Equal(firstValue, secondValue);
        Assert.Contains(plainRows, row => row.Contains("Automatically compact context", StringComparison.Ordinal));
        Assert.Contains(plainRows, row => row.Contains($"1/{TerminalSettingsPicker.Settings.Length}", StringComparison.Ordinal));
        Assert.Contains(plainRows, row => row.Contains("Type to filter", StringComparison.Ordinal));
        Assert.DoesNotContain(plainRows, row => row.Contains("Search:", StringComparison.Ordinal));
        Assert.All(rendered, row => Assert.True(TerminalTextLayout.Width(row) <= width));
        Assert.Equal(TerminalSettingsListAction.Continue,
            list.HandleInput(new(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), null)));
        Assert.Equal("images.autoResize", list.Selected?.Key);
        Assert.Equal(TerminalSettingsListAction.Continue,
            list.HandleInput(new(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false), null)));
        Assert.Equal("compaction.enabled", list.Selected?.Key);

        list.HandleInput(new(null, "hide thinking"));
        Assert.Equal("hideThinkingBlock", list.Selected?.Key);
        using var spaceStream = new MemoryStream([32]);
        var spaceEvent = new TerminalInput(spaceStream).Read();
        Assert.False(list.ShouldActivateSpace(spaceEvent));
        Assert.Contains(list.Render(width, 24, TerminalTheme.Default).Select(TerminalTextLayout.StripFormatting),
            row => row.Contains("Hide reasoning blocks", StringComparison.Ordinal));
        Assert.Equal(TerminalSettingsListAction.Activate,
            list.HandleInput(new(new ConsoleKeyInfo('\n', ConsoleKey.Enter, false, false, false), null)));
        Assert.Equal(TerminalSettingsListAction.Continue,
            list.HandleInput(new(new ConsoleKeyInfo('\u0015', ConsoleKey.U, false, false, true), null)));
        Assert.True(list.ShouldActivateSpace(spaceEvent));
        Assert.Equal(TerminalSettingsListAction.Activate, list.HandleInput(spaceEvent));
        Assert.Equal(TerminalSettingsListAction.ChangeScope,
            list.HandleInput(new(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false), null)));
        Assert.Equal(TerminalSettingsListAction.ResetValue,
            list.HandleInput(new(new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false), null)));
        Assert.Equal(TerminalSettingsListAction.Cancel,
            list.HandleInput(new(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false), null)));
    }

    [Fact]
    public void TreeFilterModeRowMatchesThePinnedLabelDescriptionValueAndDefaults()
    {
        var setting = TerminalSettingsPicker.Settings.Single(item => item.Id == "treeFilterMode");
        Assert.Equal("Tree filter mode", setting.Label);
        Assert.Equal("Default filter when opening /tree", setting.Description);
        Assert.False(setting.UserOnly);

        var unset = TerminalSettingsPicker.ToListItem(setting, new UserSettings(), new UserSettings(),
            projectScope: false, activeThemeSetting: null);
        Assert.Equal("inherit/default (default)", unset.Value);

        var configured = TerminalSettingsPicker.ToListItem(setting,
            new UserSettings(TreeFilterMode: new("labeled-only")),
            new UserSettings(TreeFilterMode: new("labeled-only")), projectScope: false, activeThemeSetting: null);
        Assert.Equal("labeled-only", configured.Value);

        var inherited = TerminalSettingsPicker.ToListItem(setting, new UserSettings(),
            new UserSettings(TreeFilterMode: new("all")), projectScope: true, activeThemeSetting: null);
        Assert.Equal("all (inherited)", inherited.Value);

        // Pinned Pi's settings list cycles these values in source order.
        Assert.Equal(["default", "no-tools", "user-only", "labeled-only", "all"],
            TreeFilterModeSetting.Values);
    }

    [Fact]
    public void DoubleEscapeActionRowMatchesThePinnedLabelDescriptionValueAndDefaults()
    {
        var setting = TerminalSettingsPicker.Settings.Single(item => item.Id == "doubleEscapeAction");
        Assert.Equal("Double-escape action", setting.Label);
        Assert.Equal("Action when pressing Escape twice with empty editor", setting.Description);
        Assert.False(setting.UserOnly);

        var unset = TerminalSettingsPicker.ToListItem(setting, new UserSettings(), new UserSettings(),
            projectScope: false, activeThemeSetting: null);
        Assert.Equal("inherit/default (default)", unset.Value);

        var configured = TerminalSettingsPicker.ToListItem(setting,
            new UserSettings(DoubleEscapeAction: new("fork")),
            new UserSettings(DoubleEscapeAction: new("fork")), projectScope: false, activeThemeSetting: null);
        Assert.Equal("fork", configured.Value);

        var inherited = TerminalSettingsPicker.ToListItem(setting, new UserSettings(),
            new UserSettings(DoubleEscapeAction: new("none")), projectScope: true, activeThemeSetting: null);
        Assert.Equal("none (inherited)", inherited.Value);

        // A malformed stored value is shown exactly as stored (no boolean relabelling), and the
        // runtime still receives it.
        foreach (var raw in new[] { "42", "false", "{}", "[]", "bogus" })
        {
            var malformed = TerminalSettingsPicker.ToListItem(setting,
                new UserSettings(DoubleEscapeAction: new(raw)),
                new UserSettings(DoubleEscapeAction: new(raw)), projectScope: false, activeThemeSetting: null);
            Assert.Equal(raw, malformed.Value);
        }
        var nullStored = TerminalSettingsPicker.ToListItem(setting,
            new UserSettings(DoubleEscapeAction: new(null)),
            new UserSettings(DoubleEscapeAction: new(null)), projectScope: false, activeThemeSetting: null);
        Assert.Equal("inherit/default (default)", nullStored.Value);

        // Pinned Pi's settings list cycles these values in source order.
        Assert.Equal(["tree", "fork", "none"], DoubleEscapeActionSetting.Values);
    }
}
