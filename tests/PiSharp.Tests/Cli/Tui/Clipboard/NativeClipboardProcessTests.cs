using System.Diagnostics;
using System.Text;
using PiSharp.Cli.Tui;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class NativeClipboardProcessTests
{
    [Fact]
    public async Task NativeImageClipboardRoundTripsOnMacOSAndWindows()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "pisharp-native-clipboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, "image.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="));

        try
        {
            await SetClipboardImageAsync(imagePath);
            var image = await new TerminalClipboard().ReadImageAsync();
            Assert.NotNull(image);
            Assert.Equal("image/png", image.MimeType);
            using var bitmap = SKBitmap.Decode(image.Bytes);
            Assert.Equal((1, 1), (bitmap!.Width, bitmap.Height));

            if (OperatingSystem.IsMacOS())
            {
                await SetClipboardFilePathAsync(imagePath);
                var paths = await new TerminalClipboard().ReadFilePathsAsync();
                Assert.Equal(new[] { imagePath }, paths);
            }
        }
        finally
        {
            try { await ClearClipboardAsync(); }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static Task SetClipboardImageAsync(string path) => OperatingSystem.IsMacOS()
        ? RunAsync("osascript", ["-e", "on run argv\nset imageData to (read (POSIX file (item 1 of argv)) as «class PNGf»)\nset the clipboard to imageData\nend run", path])
        : RunAsync("powershell.exe", ["-NoProfile", "-STA", "-Command",
            "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; " +
            "$image=[System.Drawing.Image]::FromFile($args[0]); try { [System.Windows.Forms.Clipboard]::SetImage($image) } finally { $image.Dispose() }", path]);

    private static Task SetClipboardFilePathAsync(string path) =>
        RunAsync("osascript", ["-e", "on run argv\nset the clipboard to (POSIX file (item 1 of argv))\nend run", path]);

    private static Task ClearClipboardAsync() => OperatingSystem.IsMacOS()
        ? RunAsync("osascript", ["-e", "set the clipboard to \"\""])
        : RunAsync("powershell.exe", ["-NoProfile", "-STA", "-Command",
            "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Clipboard]::Clear()"]);

    private static async Task RunAsync(string command, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {command}.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, $"{command} failed: {await error}{await output}");
    }
}
