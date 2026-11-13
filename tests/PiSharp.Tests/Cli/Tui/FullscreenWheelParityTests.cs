using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

[Collection("Console writers")]
public sealed class FullscreenWheelParityTests
{
    private static readonly JsonElement Oracle = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "wheel-oracle.json"))).RootElement.Clone();
    private static readonly Lazy<Task<JsonElement>> Observed = new(async () => JsonSerializer.SerializeToElement(
        await FullscreenWheelProbe.ObserveAsync(Oracle.GetProperty("corpus"))));

    public static IEnumerable<object[]> Cases()
    {
        foreach (var group in new[] { "settings", "sequences", "viewports", "overlays" })
            foreach (var item in Oracle.GetProperty("pi").GetProperty(group).EnumerateArray())
                yield return [group, item.GetProperty("id").GetString()!];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ExecutablePinnedCorpusMatches(string group, string id)
    {
        var expected = Oracle.GetProperty("pi").GetProperty(group).EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        var actual = (await Observed.Value).GetProperty(group).EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        if (id.StartsWith("duplicate-", StringComparison.Ordinal))
        {
            Assert.False(actual.GetProperty("loaded").GetBoolean());
            Assert.Contains("duplicate property", actual.GetProperty("error").GetString());
            return;
        }
        foreach (var field in expected.EnumerateObject())
        {
            if (field.Name == "mergedRaw" || group == "viewports" && field.Name == "maximum" && id == "regular-100") continue;
            Assert.True(actual.TryGetProperty(field.Name, out var value), $"{id} missing {field.Name}");
            Assert.True(JsonElement.DeepEquals(field.Value, value), $"{id}.{field.Name}: expected {field.Value}; actual {value}");
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"bogus\"")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("1e400")]
    public async Task PresentMalformedProjectValueOverridesWhileAbsentInherits(string raw)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-wheel-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"fullscreenWheelScrollLines\":7}");
            await File.WriteAllTextAsync(Path.Combine(root, ".pi/settings.json"), "{\"fullscreenWheelScrollLines\":" + raw + "}");
            foreach (var trusted in new[] { false, true })
            {
                using var configuration = await ProjectRuntimeConfiguration.LoadAsync(root, root,
                    CliArguments.Parse(["--offline", "--no-extensions"]), new ProjectTrust(root),
                    interactiveTrust: false, TextReader.Null, TextWriter.Null, trustedOverride: trusted);
                Assert.Equal(trusted ? FullscreenWheelScrollSetting.Automatic : FullscreenWheelScrollSetting.Fixed(7),
                    configuration.Settings.FullscreenWheelScrollLines);
                Assert.Equal(trusted, configuration.ProjectSettings is not null);
            }
            var user = await UserSettings.LoadAsync(root, _ => null);
            await UserSettingsWriter.SetAsync(Path.Combine(root, ".pi/settings.json"), "fullscreenWheelScrollLines", null, userScope: false);
            var cleared = await UserSettings.LoadProjectAsync(root);
            Assert.Null(cleared.FullscreenWheelScrollLines);
            Assert.Equal(FullscreenWheelScrollSetting.Fixed(7), user.Overlay(cleared).FullscreenWheelScrollLines);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(null, "auto (default)", false)]
    [InlineData(7, "7", false)]
    [InlineData(null, "7 (inherited)", true)]
    public void ScopeUiRetainsResolvedDefaultAndInheritance(int? configured, string expected, bool projectScope)
    {
        var setting = TerminalSettingsPicker.Settings.Single(x => x.Id == "fullscreenWheelScrollLines");
        var scoped = new UserSettings(FullscreenWheelScrollLines: configured is { } n ? FullscreenWheelScrollSetting.Fixed(n) : null);
        var effective = projectScope ? new UserSettings(FullscreenWheelScrollLines: FullscreenWheelScrollSetting.Fixed(7)) : scoped;
        Assert.Equal(expected, TerminalSettingsPicker.ToListItem(setting, scoped, effective, projectScope, null).Value);
        Assert.True(Array.FindIndex(TerminalSettingsPicker.Settings, x => x.Id == setting.Id) <
            Array.FindIndex(TerminalSettingsPicker.Settings, x => x.Id == "theme"));
        var picker = RuntimeHelpers.GetUninitializedObject(typeof(TerminalSettingsPicker));
        var choices = (IEnumerable<(string Key, string? Value, string Label, string Description)>)typeof(TerminalSettingsPicker)
            .GetMethod("Values", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, [setting, configured?.ToString() ?? "auto"])!;
        Assert.Equal(("__inherit", null, "Inherit / default", "Remove this override in the selected scope."), choices.First());
    }

    [Fact]
    public async Task NormalizedCanonicalWritesMatchPinnedSetterAndSurviveReload()
    {
        var expected = Oracle.GetProperty("pi").GetProperty("setters");
        var actual = (await Observed.Value).GetProperty("setters");
        Assert.True(JsonElement.DeepEquals(expected, actual), actual.ToString());
    }

    [Theory]
    [InlineData("{}", 1, 5)]
    [InlineData("{\"fullscreenWheelScrollLines\":\"auto\"}", 1, 5)]
    [InlineData("{\"fullscreenWheelScrollLines\":null}", 1, 5)]
    [InlineData("{\"fullscreenWheelScrollLines\":5}", 5, 5)]
    public async Task LoadedSettingReachesTheActualPresentationConsumer(string raw, int first, int next)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-wheel-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), raw);
            var settings = await UserSettings.LoadAsync(directory, _ => null);
            var clock = new ManualTimeProvider();
            using var output = new StringWriter();
            using var error = new StringWriter();
            using var screen = FullscreenWheelProbe.Screen(output, error, clock, true);
            screen.Output.Write(string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}")));
            screen.SetFullscreenWheelScrollLines(FullscreenWheelScrollSetting.Fixed(100));
            var presentation = (TerminalPresentation)RuntimeHelpers.GetUninitializedObject(typeof(TerminalPresentation));
            typeof(TerminalPresentation).GetField("_terminalScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(presentation, screen);
            presentation.ApplyTuiSettings(settings);
            screen.HandleMouse(new(64, 3, 2, false));
            Assert.Equal(first, screen.TranscriptScrollOffset);
            clock.Advance(TimeSpan.FromMilliseconds(20));
            screen.HandleMouse(new(64, 3, 2, false));
            Assert.Equal(first + next, screen.TranscriptScrollOffset);
        }
        finally { Directory.Delete(directory, true); }
    }
}
