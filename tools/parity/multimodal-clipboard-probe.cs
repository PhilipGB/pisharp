using System.Text.Json;
using PiSharp.Cli.Tui;

const string pngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==";
var png = Convert.FromBase64String(pngBase64);
var results = new List<object>();

await CaptureAsync("wayland-prefers-png", (name, arguments) =>
{
    if (name != "wl-paste") return null;
    return arguments.Contains("--list-types")
        ? new(0, "text/plain\nimage/jpeg\nimage/png\n"u8.ToArray())
        : new(0, png);
});

await CaptureAsync("wayland-empty-does-not-use-stale-x11", (name, arguments) =>
    name == "wl-paste" && arguments.Contains("--list-types")
        ? new(0, "text/plain\n"u8.ToArray())
        : new(0, png));

await CaptureAsync("wayland-failure-falls-back-to-x11", (name, arguments) =>
{
    if (name == "wl-paste") return new(1, []);
    if (name == "xclip" && arguments.Contains("TARGETS"))
        return new(0, "text/plain\nimage/png\n"u8.ToArray());
    return name == "xclip" ? new(0, png) : null;
});

string[] filePaths = ["/tmp/screenshot.png", "/tmp/My Photos/photo.png"];
var pathCommands = new CapturingCommandRunner((name, _) => name == "osascript"
    ? new(0, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(filePaths)))
    : new(1, []));
var macClipboard = new TerminalClipboard(pathCommands, platform: ClipboardPlatform.MacOS);
var editor = new TerminalEditor();
editor.Prefill("Review:");
await TerminalClipboardActions.PasteImageAsync(macClipboard, editor);
var pathPrecedence = new
{
    draft = editor.Draft,
    imageFallbackReached = pathCommands.Calls.Count != 1,
    textFallbackReached = pathCommands.Calls.Count != 1 || pathCommands.Calls[0].Command != "osascript"
};

await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(new { acquisitions = results, filePathPrecedence = pathPrecedence }));

async Task CaptureAsync(string id, Func<string, IReadOnlyList<string>, ClipboardCommandResult?> response)
{
    var commands = new CapturingCommandRunner(response);
    var clipboard = new TerminalClipboard(commands,
        name => name switch { "WAYLAND_DISPLAY" => "wayland-test", "DISPLAY" => ":0", _ => null },
        platform: ClipboardPlatform.Linux);
    var image = await clipboard.ReadImageAsync();
    results.Add(new
    {
        id,
        mimeType = image?.MimeType,
        data = image is null ? null : Convert.ToBase64String(image.Bytes),
        calls = commands.Calls.Select(call => new object[] { call.Command, call.Arguments })
    });
}

sealed class CapturingCommandRunner(Func<string, IReadOnlyList<string>, ClipboardCommandResult?> response)
    : IClipboardCommandRunner
{
    public List<Call> Calls { get; } = [];

    public Task<ClipboardCommandResult?> RunAsync(string command, IReadOnlyList<string> arguments, string? input,
        int maximumOutputBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Calls.Add(new(command, arguments.ToArray()));
        return Task.FromResult(response(command, arguments));
    }

    public sealed record Call(string Command, IReadOnlyList<string> Arguments);
}
