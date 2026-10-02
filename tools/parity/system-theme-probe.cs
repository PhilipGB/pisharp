using System.Globalization;
using System.Reflection;
using System.Text.Json;
using PiSharp.Cli.Tui;

var results = new List<object>();
var foregrounds = (string[])typeof(TerminalTheme).GetField("s_foregroundTokens", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
var backgrounds = (string[])typeof(TerminalTheme).GetField("s_backgroundTokens", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
foreach (var scenario in JsonSerializer.Deserialize<JsonElement[]>(args[0])!)
{
    var state = new TerminalColorState(Color(scenario, "foreground"), Color(scenario, "background"),
        scenario.TryGetProperty("palette", out var palette) ? palette.EnumerateArray().Select(entry => Rgb(entry.GetString()!)).ToArray() : null);
    var theme = TerminalSystemTheme.Create(scenario.GetProperty("mode").GetString() == "truecolor" ? TerminalColorMode.TrueColor : TerminalColorMode.Ansi256, state,
        scenario.TryGetProperty("appearance", out var appearance) ? appearance.GetString()! : "dark");
    var colors = foregrounds.ToDictionary(token => token, theme.Fg, StringComparer.Ordinal);
    foreach (var token in backgrounds) colors[token] = theme.Bg(token);
    results.Add(new { id = scenario.GetProperty("id").GetString(), appearance = theme.Appearance, colors });
}
Console.WriteLine(JsonSerializer.Serialize(results));

static TerminalTheme.Rgb? Color(JsonElement scenario, string name) =>
    scenario.TryGetProperty(name, out var value) ? Rgb(value.GetString()!) : null;
static TerminalTheme.Rgb Rgb(string hex) => new(
    byte.Parse(hex[1..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
    byte.Parse(hex[3..5], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
    byte.Parse(hex[5..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
