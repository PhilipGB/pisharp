using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

/// <summary>
/// Deterministic double-escape contract read from the pinned-Pi oracle. The injectable clock owns
/// the 500 ms boundary; the PTY fixtures only confirm the end-to-end dispatch.
/// </summary>
public sealed class DoubleEscapeActionOracleTests
{
    private static readonly string OraclePath = Path.Combine(RepositoryRoot(), "docs", "parity", "fixtures",
        "double-escape-action-pi-oracle-2026-11-12.json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static JsonDocument LoadOracle() => JsonDocument.Parse(File.ReadAllText(OraclePath));

    public static TheoryData<string> ScenarioNames()
    {
        using var document = LoadOracle();
        var data = new TheoryData<string>();
        foreach (var property in document.RootElement.GetProperty("scenarios").EnumerateObject())
            data.Add(property.Name);
        return data;
    }

    [Fact]
    public void OracleInventoryMatchesThePinnedSources()
    {
        using var document = LoadOracle();
        var root = document.RootElement;
        Assert.Equal("b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6", root.GetProperty("piSha").GetString());
        Assert.Equal("tree", root.GetProperty("default").GetString());
        Assert.Equal(500, root.GetProperty("windowMilliseconds").GetInt32());
        Assert.Equal("<", root.GetProperty("windowComparison").GetString());
        Assert.Equal("Double-escape action", root.GetProperty("label").GetString());
        Assert.Equal("Action when pressing Escape twice with empty editor", root.GetProperty("description").GetString());
        Assert.Equal(new[] { "tree", "fork", "none" },
            root.GetProperty("values").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(["tree", "fork", "none"], DoubleEscapeActionSetting.Values);
        var paths = root.GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("file").GetString()).ToArray();
        Assert.Contains("packages/coding-agent/src/core/settings-manager.ts", paths);
        Assert.Contains("packages/coding-agent/src/modes/interactive/interactive-mode.ts", paths);
        Assert.Contains("packages/coding-agent/src/modes/interactive/components/settings-selector.ts", paths);
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task MatchesPinnedPi(string name)
    {
        using var document = LoadOracle();
        var scenario = document.RootElement.GetProperty("scenarios").GetProperty(name);
        var editor = new TerminalEditor();
        var text = scenario.GetProperty("text").GetString()!;
        if (text.Length > 0) editor.Prefill(text);
        var now = 0L;
        editor.SetDoublePressClock(() => now);
        var actions = new List<string>();

        var presses = scenario.TryGetProperty("presses", out var scripted)
            ? scripted.EnumerateArray().Select(press => (
                At: press.GetProperty("at").GetInt64(),
                Setting: ReadSetting(press.GetProperty("setting")))).ToArray()
            : scenario.GetProperty("escapeAt").EnumerateArray()
                .Select(at => (At: at.GetInt64(), Setting: ReadSetting(scenario.GetProperty("setting")))).ToArray();

        foreach (var press in presses)
        {
            now = press.At;
            editor.SetDoubleEscapeActionProvider(() => press.Setting);
            Assert.True(await editor.HandleIdleEscapeAsync(action =>
            {
                actions.Add(action);
                return Task.CompletedTask;
            }));
        }

        var expected = scenario.GetProperty("expected");
        Assert.Equal(expected.GetProperty("text").GetString(), editor.Draft);
        Assert.Equal(expected.GetProperty("actions").EnumerateArray().Select(item => item.GetString()), actions);
    }

    /// <summary>Maps a stored value onto the effective action, mirroring UserSettings' resolution.</summary>
    private static string ReadSetting(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new DoubleEscapeActionSettingValue(value.GetString()).Action,
        JsonValueKind.Null => new DoubleEscapeActionSettingValue(null).Action,
        JsonValueKind.Undefined => new DoubleEscapeActionSettingValue(null).Action,
        _ => new DoubleEscapeActionSettingValue(value.GetRawText()).Action
    };

    [Theory]
    [InlineData("bogus", "bogus")]
    [InlineData("", "")]
    [InlineData("42", "42")]
    [InlineData("false", "false")]
    [InlineData("{}", "{}")]
    [InlineData("[]", "[]")]
    [InlineData("null", "tree")]
    public async Task StoredValuesReachTheHandlerThroughTheLoader(string storedValue, string effective)
    {
        // The oracle scenario drives the handler with the effective value; this ties the stored
        // JSON literal to that value through the real settings loader.
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-double-escape-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var literal = storedValue is "bogus" or "" ? JsonSerializer.Serialize(storedValue) : storedValue;
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"),
                "{\"doubleEscapeAction\":" + literal + "}");
            var settings = await UserSettings.LoadAsync(directory, _ => null);
            Assert.Equal(effective, settings.EffectiveDoubleEscapeAction);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CompletionCancellationDoesNotArmTheDoublePressTimer()
    {
        // Pinned Pi forwards Escape to the editor while autocomplete is visible, so onEscape never
        // runs and the timer keeps its previous value.
        var editor = new TerminalEditor();
        editor.Prefill("/mod");
        await editor.HandleActiveInputAsync(new(new('\t', ConsoleKey.Tab, false, false, false), null),
            (_, _, _) => Task.FromResult(true), () => [], () => { });
        editor.SetDoubleEscapeActionProvider(() => "tree");
        var now = 1_000L;
        editor.SetDoublePressClock(() => now);
        var actions = new List<string>();

        // The cancel press must not arm the timer: the next press records the first time, so
        // only the third press completes a pair.
        await editor.HandleIdleEscapeAsync(Dispatch);
        Assert.Empty(actions);
        editor.Prefill("");
        now += 100;
        await editor.HandleIdleEscapeAsync(Dispatch);
        Assert.Empty(actions);
        now += 100;
        await editor.HandleIdleEscapeAsync(Dispatch);

        Assert.Equal(["app.session.tree"], actions);

        Task Dispatch(string action)
        {
            actions.Add(action);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task BashEntryEscapeClearsTheDraftWithoutTouchingTheTimer()
    {
        var editor = new TerminalEditor();
        editor.Prefill("!echo hi");
        editor.SetDoubleEscapeActionProvider(() => "tree");
        var now = 1_000L;
        editor.SetDoublePressClock(() => now);
        var actions = new List<string>();

        await editor.HandleIdleEscapeAsync(Dispatch);
        Assert.Equal("", editor.Draft);
        editor.Prefill("");
        now += 100;
        await editor.HandleIdleEscapeAsync(Dispatch);
        Assert.Empty(actions);
        now += 100;
        await editor.HandleIdleEscapeAsync(Dispatch);

        Assert.Equal(["app.session.tree"], actions);

        Task Dispatch(string action)
        {
            actions.Add(action);
            return Task.CompletedTask;
        }
    }
}
