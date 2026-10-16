using System.Text.Json;
using PiSharp.Cli.Tui;

using var sources = JsonDocument.Parse(args[0]);
var results = new List<object>();
foreach (var scenario in sources.RootElement.EnumerateArray())
{
    var platform = scenario.GetProperty("platform").GetString()!;
    var force = scenario.GetProperty("force").GetString()!;
    var order = new List<string>();
    var clipboard = new TerminalClipboard(new Commands(order), name => name switch
    {
        "PI_CLIPBOARD_OSC52" => force,
        "DISPLAY" when platform == "linux" => ":99",
        _ => null
    }, order.Add, platform switch
    {
        "darwin" => ClipboardPlatform.MacOS,
        "win32" => ClipboardPlatform.Windows,
        _ => ClipboardPlatform.Linux
    });
    await clipboard.CopyTextAsync("hello 😀");
    results.Add(new { platform, force, order });
}
Console.WriteLine(JsonSerializer.Serialize(results));

sealed class Commands(List<string> order) : IClipboardCommandRunner
{
    public Task<ClipboardCommandResult?> RunAsync(string command, IReadOnlyList<string> arguments,
        string? input, int maximumOutputBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        order.Add("native");
        return Task.FromResult<ClipboardCommandResult?>(new(0, []));
    }
}
