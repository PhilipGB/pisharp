using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalSettingsPickerTests
{
    [Fact]
    public void SettingsKeepPiSharedOrderAndAppendPiSharpOptions()
    {
        // Pi source receipt: settings-selector.ts, main, 2026-10-16
        // https://github.com/earendil-works/pi/blob/main/packages/coding-agent/src/modes/interactive/components/settings-selector.ts
        // The prompt-specified first ten are authoritative; the remaining supported shared entries
        // retain upstream order. Mermaid rendering, cache miss notices and changelog collapsing are
        // out of scope because their renderer, cache diagnostics and update notification owners are absent.
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
            "hideThinkingBlock", "quietStartup", "defaultProjectTrust", "theme"
        }, ids.Skip(10).Take(8));
        Assert.Equal(new[]
        {
            "defaultThinkingLevel", "httpProxy", "terminal.trueColor", "externalEditor",
            "retry.provider.timeoutMs", "retry.provider.maxRetries",
            "retry.provider.maxRetryDelayMs", "markdown.codeBlockIndent"
        }, ids.Skip(18));
        Assert.DoesNotContain(ids, id => id is "markdown.mermaid" or "showCacheMissNotices" or "collapseChangelog");
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
        Assert.Contains(plainRows, row => row.Contains("1/26", StringComparison.Ordinal));
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
}
