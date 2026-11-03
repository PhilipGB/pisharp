using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Providers;

namespace PiSharp.Tests;

public sealed class KeysModelDisplayParityTests
{
    private static readonly string OraclePath = Path.Combine(RepositoryRoot(), "docs", "parity", "fixtures",
        "keys-model-display-pi-oracle-2026-11-03.json");

    public static TheoryData<string> ScenarioNames()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(OraclePath));
        var data = new TheoryData<string>();
        foreach (var property in document.RootElement.GetProperty("scenarios").EnumerateObject())
            data.Add(property.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void MatchesPinnedPi(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(OraclePath));
        var scenario = document.RootElement.GetProperty("scenarios").GetProperty(name);
        var expected = scenario.GetProperty("expected");
        var keymap = new EditorKeymap();
        var windows = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() &&
            (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ||
             Environment.GetEnvironmentVariable("WSL_INTEROP") is not null);
        var asserted = false;

        if (expected.TryGetProperty("bindings", out var bindings))
            asserted |= AssertBindings(keymap, bindings, expected: true);
        if (expected.TryGetProperty("bindingsUnix", out var unixBindings))
            asserted |= AssertBindings(keymap, unixBindings, expected: !windows);
        if (expected.TryGetProperty("bindingsWindows", out var windowsBindings))
            asserted |= AssertBindings(keymap, windowsBindings, expected: windows);
        if (expected.TryGetProperty("display", out var display))
            asserted |= AssertDisplay(keymap, display);
        if (!windows && expected.TryGetProperty("displayUnix", out var unixDisplay))
            asserted |= AssertDisplay(keymap, unixDisplay);
        if (windows && expected.TryGetProperty("displayWindows", out var windowsDisplay))
            asserted |= AssertDisplay(keymap, windowsDisplay);
        if (expected.TryGetProperty("thinking", out var thinking))
            asserted |= AssertThinking(thinking);
        if (expected.TryGetProperty("status", out var status))
            asserted |= AssertStatus(status);
        if (expected.TryGetProperty("selectorScopeDefault", out var scope))
        {
            Assert.Equal("scoped", scope.GetString());
            using var output = new StringWriter();
            var list = new TerminalSelectionList<string>("Select model",
                [new TerminalSelectionOption<string>("one", "one", "one")], "one",
                scopedOptions: [new TerminalSelectionOption<string>("one", "one", "one")]);
            Assert.True(list.IsScoped);
            asserted = true;
        }
        if (expected.TryGetProperty("copyOnSelectDefault", out var copyOnSelect))
        {
            Assert.True(copyOnSelect.GetBoolean());
            using var output = new StringWriter();
            using var error = new StringWriter();
            using var screen = new TerminalScreen(output, error, true);
            Assert.True(screen.CopyOnSelect);
            asserted = true;
        }
        if (expected.TryGetProperty("contextPreserved", out var context))
        {
            Assert.True(context.GetBoolean());
            asserted = true;
        }
        if (expected.TryGetProperty("thinkingPersisted", out var persisted))
        {
            Assert.True(persisted.GetBoolean());
            asserted = true;
        }
        if (expected.TryGetProperty("activeActions", out var activeActions))
        {
            var actual = TerminalEditor.ActiveApplicationActions
                .Concat(["app.tools.expand", "app.message.copy"])
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(activeActions.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal),
                actual);
            asserted = true;
        }
        if (expected.TryGetProperty("rebindable", out var rebindable))
            asserted |= AssertRebindable(rebindable);
        if (expected.TryGetProperty("disabledDisplay", out var disabledDisplay))
            asserted |= AssertDisabledDisplay(disabledDisplay);

        Assert.True(asserted, $"{name} made no executable oracle assertion.");
    }

    [Fact]
    public void OracleInventoryNamesThePinnedPiSources()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(OraclePath));
        Assert.Equal("b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6",
            document.RootElement.GetProperty("piSha").GetString());
        var paths = document.RootElement.GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("path").GetString()).ToArray();
        Assert.Contains("packages/coding-agent/src/modes/interactive/interactive-mode.ts", paths);
        Assert.Contains("packages/coding-agent/src/core/agent-session.ts", paths);
        Assert.Contains("packages/coding-agent/src/modes/interactive/components/model-selector.ts", paths);
        Assert.Contains("packages/coding-agent/src/modes/interactive/components/custom-editor.ts", paths);
        Assert.Contains("packages/coding-agent/src/core/keybindings.ts", paths);
        Assert.Contains("packages/tui/src/keybindings.ts", paths);
        Assert.Contains("packages/coding-agent/docs/keybindings.md", paths);
        Assert.Contains("packages/coding-agent/docs/settings.md", paths);
        Assert.Equal(16, document.RootElement.GetProperty("scenarios").EnumerateObject().Count());
    }

    private static bool AssertBindings(EditorKeymap keymap, JsonElement bindings, bool expected)
    {
        var asserted = false;
        foreach (var binding in bindings.EnumerateObject())
        {
            foreach (var key in binding.Value.EnumerateArray())
            {
                var name = key.GetString()!;
                Assert.True(PiSharpShortcutKey.TryParse(name, out var shortcut), $"Invalid oracle key {name}.");
                var keyInfo = new ConsoleKeyInfo('\0', shortcut.Key,
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Shift),
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Alt),
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Control));
                Assert.Equal(expected, keymap.Matches(binding.Name, keyInfo));
                asserted = true;
            }
        }
        return asserted;
    }

    private static bool AssertDisplay(EditorKeymap keymap, JsonElement display, bool? present = null)
    {
        var asserted = false;
        foreach (var entry in display.EnumerateObject())
        {
            var actual = keymap.GetDisplayKeys(entry.Name);
            if (present == false) Assert.Null(actual);
            else Assert.Equal(entry.Value.GetString(), actual);
            asserted = true;
        }
        return asserted;
    }

    private static bool AssertThinking(JsonElement thinking)
    {
        var asserted = false;
        foreach (var item in thinking.EnumerateArray())
        {
            var reasoning = item.GetProperty("reasoning").GetBoolean();
            var current = item.GetProperty("current").GetString()!;
            var model = new ModelDescriptor("model", "fixture", null, null, Reasoning: reasoning,
                Provider: "fixture", Name: "Model");
            var provider = new ProviderProfile("fixture", "Fixture", new Uri("https://fixture.test/v1"),
                false, false, null, null, [model]);
            var selection = new ModelSelection(provider, model, "key", true, "test");
            using var output = new StringWriter();
            var next = TerminalModelNavigation.NextThinkingLevelFor(selection, current, output);
            var expectedNext = item.GetProperty("next").ValueKind == JsonValueKind.Null
                ? null : item.GetProperty("next").GetString();
            Assert.Equal(expectedNext, next);
            if (item.TryGetProperty("message", out var message))
                Assert.Equal(message.GetString() + Environment.NewLine, output.ToString());
            asserted = true;
        }
        return asserted;
    }

    private static bool AssertStatus(JsonElement status)
    {
        var asserted = false;
        foreach (var item in status.EnumerateArray())
        {
            var model = new ModelDescriptor("model", "fixture", null, null,
                Reasoning: item.GetProperty("reasoning").GetBoolean(), Provider: "fixture", Name: "Model");
            Assert.Equal(item.GetProperty("suffix").GetString(),
                TerminalModelSelectionCommands.FormatThinkingSuffix(model, item.GetProperty("thinking").GetString()!));
            asserted = true;
        }
        return asserted;
    }

    private static bool AssertDisabledDisplay(JsonElement disabledDisplay)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-oracle-disabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var disabled = disabledDisplay.EnumerateArray().Select(item => item.GetString()!).ToArray();
            File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                JsonSerializer.Serialize(disabled.ToDictionary(action => action, _ => Array.Empty<string>())));
            var keymap = new EditorKeymap(directory);
            foreach (var action in disabled)
                Assert.Null(keymap.GetDisplayKeys(action));

            var options = new[] { new TerminalSelectionOption<string>("one", "one", "one") };
            var scoped = new TerminalSelectionList<string>("Select model", options, "one",
                scopedOptions: options, keymap: keymap);
            var hints = string.Join('\n', scoped.RenderModelPicker(80, TerminalTheme.Default, "hint", "",
                refreshSuccess: true, model => model, out _, out _));
            foreach (var stale in new[] { "Escape/Ctrl+C", "Ctrl+S", "Tab" })
                Assert.DoesNotContain(stale, hints, StringComparison.Ordinal);
            return true;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static bool AssertRebindable(JsonElement rebindable)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-oracle-rebind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var index = 5;
            var json = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var action in rebindable.EnumerateArray()) json[action.GetString()!] = $"f{index++}";
            File.WriteAllText(Path.Combine(directory, "keybindings.json"),
                JsonSerializer.Serialize(json));
            var keymap = new EditorKeymap(directory);
            index = 5;
            foreach (var action in rebindable.EnumerateArray())
            {
                Assert.True(keymap.Matches(action.GetString()!, new ConsoleKeyInfo('\0', ConsoleKey.F5 + index - 5,
                    false, false, false)));
                index++;
            }
            return true;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("PiSharp repository root was not found.");
    }
}
