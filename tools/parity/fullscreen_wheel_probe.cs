using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;
using PiSharp.Tests;

internal static class FullscreenWheelProbe
{
    internal static async Task<object> ObserveAsync(JsonElement corpus)
    {
        var settings = new List<object>();
        foreach (var item in corpus.GetProperty("settings").EnumerateArray())
        {
            var root = Path.Combine(Path.GetTempPath(), "pisharp-wheel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".pi"));
            var id = item.GetProperty("id").GetString();
            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), item.GetProperty("userRaw").GetString());
                await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), item.GetProperty("projectRaw").GetString());
                var user = await UserSettings.LoadAsync(root, _ => null);
                var project = item.GetProperty("projectTrusted").GetBoolean() ? await UserSettings.LoadProjectAsync(root) : null;
                var effective = user.Overlay(project ?? new());
                var value = effective.FullscreenWheelScrollLines ?? FullscreenWheelScrollSetting.Automatic;
                var setting = TerminalSettingsPicker.Settings.Single(x => x.Id == "fullscreenWheelScrollLines");
                var row = TerminalSettingsPicker.ToListItem(setting, effective, effective, false, null);
                var picker = RuntimeHelpers.GetUninitializedObject(typeof(TerminalSettingsPicker));
                var choices = (IEnumerable)typeof(TerminalSettingsPicker).GetMethod("Values", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(picker, [setting, value.ToSettingValue()])!;
                var options = choices.Cast<(string Key, string? Value, string Label, string Description)>()
                    .Where(x => x.Value is not null).Select(x => x.Value).ToArray();
                settings.Add(new { id, loaded = true, effective = Value(value), current = row.Value.Split(" (")[0],
                    label = row.Label, description = row.Description, options });
            }
            catch (InvalidDataException e) { settings.Add(new { id, loaded = false, error = e.Message }); }
            finally { Directory.Delete(root, true); }
        }
        var sequences = corpus.GetProperty("sequences").EnumerateArray().Select(item =>
        {
            var clock = new ManualTimeProvider();
            var accelerate = item.GetProperty("accelerate").GetBoolean();
            var accelerator = new FullscreenWheelScrollAccelerator(clock, accelerate);
            accelerator.SetSetting(Parse(item.GetProperty("setting")));
            var lines = new List<int>();
            foreach (var e in item.GetProperty("events").EnumerateArray())
            {
                Advance(clock, e.GetProperty("t").GetInt32());
                if (e.TryGetProperty("set", out var change)) accelerator.SetSetting(Parse(change));
                lines.Add(accelerator.Next(e.GetProperty("direction").GetInt32()));
            }
            return new { id = item.GetProperty("id").GetString(), accelerate, lines };
        }).ToArray();
        var viewports = corpus.GetProperty("viewports").EnumerateArray().Select(Viewport).ToArray();
        var overlays = corpus.GetProperty("overlays").EnumerateArray().Select(Overlay).ToArray();
        return new { settings, sequences, viewports, overlays };
    }

    internal static object Viewport(JsonElement item)
    {
        var clock = new ManualTimeProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var regular = item.GetProperty("mode").GetString() == "regular";
        using var screen = Screen(output, error, clock, !regular);
        screen.SetFullscreenWheelScrollLines(Parse(item.GetProperty("setting")));
        screen.Output.Write(string.Join('\n', Enumerable.Range(1, 300).Select(i => $"ROW-{i:000}")));
        screen.ScrollToTop();
        var maximum = screen.TranscriptScrollOffset;
        var start = item.GetProperty("start").GetString();
        if (start is not ("top" or "near-top")) screen.ScrollToBottom();
        if (start is "near-top" or "near-bottom")
        {
            screen.SetFullscreenWheelScrollLines(FullscreenWheelScrollSetting.Fixed(2));
            screen.HandleMouse(new(start == "near-top" ? 65 : 64, 3, 2, false));
            screen.SetFullscreenWheelScrollLines(Parse(item.GetProperty("setting")));
        }
        var initial = screen.TranscriptScrollOffset;
        var events = new List<object>();
        // Independent helper reports requested distance; viewport observations come from the screen itself.
        var accelerator = new FullscreenWheelScrollAccelerator(clock, true);
        accelerator.SetSetting(Parse(item.GetProperty("setting")));
        foreach (var e in item.GetProperty("events").EnumerateArray())
        {
            Advance(clock, e.GetProperty("t").GetInt32());
            if (e.TryGetProperty("set", out var change))
            {
                screen.SetFullscreenWheelScrollLines(Parse(change));
                accelerator.SetSetting(Parse(change));
            }
            var before = screen.TranscriptScrollOffset;
            var direction = e.GetProperty("direction").GetInt32();
            var multiplier = e.GetProperty("alt").GetBoolean() ? 5 : 1;
            screen.HandleMouse(new((direction < 0 ? 64 : 65) + (multiplier == 5 ? 8 : 0), 3, 2, false));
            var after = screen.TranscriptScrollOffset;
            events.Add(new { before, lines = regular ? 0 : accelerator.Next(direction), multiplier, after, movement = after - before });
        }
        return new { id = item.GetProperty("id").GetString(), initial, maximum, events };
    }

    private static object Overlay(JsonElement item)
    {
        var clock = new ManualTimeProvider();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = Screen(output, error, clock, true);
        screen.SetFullscreenWheelScrollLines(Parse(item.GetProperty("setting")));
        screen.Output.Write(string.Join('\n', Enumerable.Range(1, 300).Select(i => $"ROW-{i:000}")));
        var alt = item.GetProperty("alt").GetBoolean();
        var wheel = $"\u001b[<{(alt ? 72 : 64)};40;12M";
        var wheelBytes = Encoding.UTF8.GetByteCount(wheel);
        using var bytes = new ObservedInput(Encoding.UTF8.GetBytes(wheel + wheel + "\r"), wheelBytes,
            () => Advance(clock, 20));
        var input = new TerminalInput(bytes);
        var deltas = new List<int?>();
        var options = Enumerable.Range(0, 60).Select(i => new TerminalSelectionOption<int>(i.ToString(), i, $"item {i}")).ToArray();
        var selected = new TerminalOverlayHost(input).Select(screen, "Wheel overlay", options, "20", handleInput: (_, e) =>
        {
            if (e.Mouse is { IsWheel: true } mouse)
                deltas.Add((int?)typeof(TerminalMouseEvent).GetProperty("FullscreenWheelDelta", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mouse));
            return false;
        });
        var movement = screen.TranscriptScrollOffset;
        Advance(clock, 40);
        screen.HandleMouse(new(64, 3, 2, false));
        return new { id = item.GetProperty("id").GetString(), selected = selected?.Option.Value, deltas,
            transcriptMovement = movement, nextTranscriptMovement = screen.TranscriptScrollOffset - movement };
    }

    internal static TerminalScreen Screen(TextWriter output, TextWriter error, ManualTimeProvider clock, bool fullscreen) =>
        new(output, error, () => 80, () => 24, new TerminalImageRenderer(), TerminalTheme.Default,
            queryTerminalColors: false, timeProvider: clock, minimumRenderInterval: TimeSpan.Zero, useAlternateScreen: fullscreen);

    private static FullscreenWheelScrollSetting Parse(JsonElement value) => FullscreenWheelScrollSetting.Parse(value);
    private static object Value(FullscreenWheelScrollSetting value) => value.Auto ? "auto" : value.Lines;
    private static void Advance(ManualTimeProvider clock, int milliseconds) =>
        clock.Advance(TimeSpan.FromMilliseconds(milliseconds) - clock.GetUtcNow().Subtract(DateTimeOffset.UnixEpoch));

    private sealed class ObservedInput(byte[] bytes, int observeAt, Action observe) : MemoryStream(bytes)
    {
        public override int ReadByte()
        {
            if (Position == observeAt) observe();
            return base.ReadByte();
        }
    }
}
