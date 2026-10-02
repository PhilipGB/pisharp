using System.Text.Json;
using PiSharp.Cli.Tui;
using var input = JsonDocument.Parse(args[0]);
var results = input.RootElement.EnumerateArray().Select(scenario => new {
    id = scenario.GetProperty("id").GetString(),
    ansi = RadiusLoginShimmer.Paint("Sign in with Radius", scenario.GetProperty("time").GetDouble(),
        scenario.GetProperty("mode").GetString() == "truecolor" ? TerminalColorMode.TrueColor : TerminalColorMode.Ansi256)
});
Console.WriteLine(JsonSerializer.Serialize(results));
