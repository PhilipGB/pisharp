using System.Text;
using System.Text.Json;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class AppControlParityTests
{
    private static readonly string OraclePath = Path.Combine(RepositoryRoot(), "docs", "parity", "fixtures",
        "keys-app-control-pi-oracle-2026-11-03.json");

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
        switch (scenario.GetProperty("operation").GetString())
        {
            case "idle-escape":
                AssertIdleEscape(name, scenario);
                break;
            case "ctrl-c":
                AssertCtrlC(name, scenario);
                break;
            case "ctrl-c-window":
                AssertCtrlCWindow(name, scenario);
                break;
            case "ctrl-d":
                AssertCtrlD(name, scenario);
                break;
            case "bindings":
                AssertBindings(name, scenario);
                break;
            case "keyboard":
                AssertKeyboard(name, scenario);
                break;
            default:
                throw new InvalidOperationException($"{name} has an unsupported oracle operation.");
        }
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
        Assert.Contains("packages/coding-agent/src/modes/interactive/components/custom-editor.ts", paths);
        Assert.Contains("packages/coding-agent/src/core/keybindings.ts", paths);
        Assert.Contains("packages/coding-agent/src/core/settings-manager.ts", paths);
        Assert.Contains("packages/coding-agent/docs/keybindings.md", paths);
        Assert.Contains("packages/coding-agent/docs/settings.md", paths);
        Assert.True(document.RootElement.GetProperty("ptyScenarios").EnumerateObject()
            .SelectMany(entry => entry.Value.EnumerateArray()).Count() >= 20);
    }

    private static void AssertIdleEscape(string name, JsonElement scenario)
    {
        var editor = new TerminalEditor();
        var text = scenario.GetProperty("text").GetString()!;
        editor.Prefill(text);
        editor.SetDoubleEscapeActionProvider(() => scenario.GetProperty("setting").GetString()!);
        var now = 0L;
        editor.SetDoublePressClock(() => now);
        var actions = new List<string>();
        foreach (var escape in scenario.GetProperty("escapeAt").EnumerateArray())
        {
            now = escape.GetInt64();
            Assert.True(editor.HandleIdleEscapeAsync(action =>
            {
                actions.Add(action);
                return Task.CompletedTask;
            }).GetAwaiter().GetResult());
        }
        var expected = scenario.GetProperty("expected");
        Assert.Equal(expected.GetProperty("text").GetString(), editor.Draft);
        Assert.Equal(expected.GetProperty("actions").EnumerateArray().Select(item => item.GetString()), actions);
    }

    private static void AssertCtrlC(string name, JsonElement scenario)
    {
        var buffer = new EditorBuffer();
        buffer.SetText(scenario.GetProperty("text").GetString()!);
        if (scenario.TryGetProperty("cursor", out var cursor)) buffer.SetCursor(cursor.GetInt32());
        Assert.Equal(EditorAction.Cancel, buffer.Handle(Key(ConsoleKey.C, control: true)));
        var expected = scenario.GetProperty("expected");
        Assert.Equal(expected.GetProperty("text").GetString(), buffer.Text);
        Assert.Equal(expected.GetProperty("cursor").GetInt32(), buffer.Cursor);
        Assert.Equal(expected.GetProperty("selection").GetString(), buffer.SelectedText);
    }

    private static void AssertCtrlCWindow(string name, JsonElement scenario)
    {
        var editor = new TerminalEditor();
        var now = 0L;
        editor.SetDoublePressClock(() => now);
        var exits = new List<bool>();
        foreach (var elapsed in scenario.GetProperty("elapsed").EnumerateArray())
        {
            now = elapsed.GetInt64();
            exits.Add(editor.RegisterClearPress());
        }
        Assert.Equal(scenario.GetProperty("expected").GetProperty("exits").EnumerateArray()
            .Select(item => item.GetBoolean()), exits);
    }

    private static void AssertCtrlD(string name, JsonElement scenario)
    {
        var buffer = new EditorBuffer();
        buffer.SetText(scenario.GetProperty("text").GetString()!);
        if (scenario.TryGetProperty("cursor", out var cursor)) buffer.SetCursor(cursor.GetInt32());
        if (scenario.TryGetProperty("selectRight", out var selections))
            for (var index = 0; index < selections.GetInt32(); index++)
                buffer.Handle(Key(ConsoleKey.RightArrow, shift: true));
        var action = buffer.Handle(Key(ConsoleKey.D, control: true));
        var expected = scenario.GetProperty("expected");
        var expectedAction = expected.GetProperty("action").GetString() switch
        {
            "exit" => EditorAction.Exit,
            "render" => EditorAction.Render,
            _ => throw new InvalidOperationException($"{name} has an unsupported Ctrl+D action.")
        };
        Assert.Equal(expectedAction, action);
        if (expected.TryGetProperty("text", out var expectedText)) Assert.Equal(expectedText.GetString(), buffer.Text);
        if (expected.TryGetProperty("cursor", out var expectedCursor)) Assert.Equal(expectedCursor.GetInt32(), buffer.Cursor);
    }

    private static void AssertBindings(string name, JsonElement scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-app-control-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bindings = scenario.GetProperty("bindings").EnumerateObject()
                .ToDictionary(pair => pair.Name,
                    pair => pair.Value.EnumerateArray().Select(item => item.GetString()!).ToArray());
            File.WriteAllText(Path.Combine(directory, "keybindings.json"), JsonSerializer.Serialize(bindings));
            var keymap = new EditorKeymap(directory);
            var expected = scenario.GetProperty("expected");
            foreach (var pair in expected.GetProperty("matches").EnumerateObject())
                Assert.True(keymap.Matches(pair.Name, ParseKey(pair.Value.GetString()!)),
                    $"{name}: {pair.Name} should match {pair.Value.GetString()}");
            foreach (var pair in expected.GetProperty("notMatches").EnumerateObject())
                Assert.False(keymap.Matches(pair.Name, ParseKey(pair.Value.GetString()!)),
                    $"{name}: {pair.Name} should not match {pair.Value.GetString()}");
            foreach (var action in expected.GetProperty("disabled").EnumerateArray())
            {
                var key = action.GetString() == "app.clear" ? Key(ConsoleKey.C, control: true) : Key(ConsoleKey.Escape);
                Assert.False(keymap.Matches(action.GetString()!, key));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void AssertKeyboard(string name, JsonElement scenario)
    {
        var legacy = new TerminalInput(new MemoryStream(Convert.FromHexString(scenario.GetProperty("legacy").GetString()!)));
        var kitty = new TerminalInput(new MemoryStream(Convert.FromHexString(scenario.GetProperty("kitty").GetString()!)));
        Assert.True(legacy.TryRead(0, out var legacyKey));
        Assert.True(kitty.TryRead(0, out var kittyKey));
        var expected = scenario.GetProperty("expected");
        Assert.Equal(expected.GetProperty("key").GetString()!.ToLowerInvariant(),
            legacyKey.Key!.Value.Key.ToString().ToLowerInvariant());
        Assert.Equal(legacyKey.Key!.Value.Key, kittyKey.Key!.Value.Key);
        Assert.Equal(legacyKey.Key!.Value.Modifiers, kittyKey.Key!.Value.Modifiers);
        var keymap = new EditorKeymap();
        Assert.True(keymap.Matches(expected.GetProperty("action").GetString()!, legacyKey.Key!.Value));
        Assert.True(keymap.Matches(expected.GetProperty("action").GetString()!, kittyKey.Key!.Value));
    }

    private static ConsoleKeyInfo ParseKey(string value)
    {
        var parts = value.Split('+');
        var key = parts[^1].ToLowerInvariant() switch
        {
            "escape" => ConsoleKey.Escape,
            "f7" => ConsoleKey.F7,
            "f8" => ConsoleKey.F8,
            "q" => ConsoleKey.Q,
            "z" => ConsoleKey.Z,
            "c" => ConsoleKey.C,
            "d" => ConsoleKey.D,
            _ => throw new InvalidOperationException($"Unsupported oracle key {value}.")
        };
        return new ConsoleKeyInfo('\0', key, parts.Contains("shift"), parts.Contains("alt"), parts.Contains("ctrl"));
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char keyChar = '\0', bool shift = false, bool alt = false,
        bool control = false) => new(keyChar, key, shift, alt, control);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("PiSharp repository root was not found.");
    }
}
