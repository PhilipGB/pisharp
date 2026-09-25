using System.Text;
using PiSharp.Cli.Tui;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class TerminalClipboardTests
{
    [Fact]
    public async Task WaylandReadReturnsEmptyInsteadOfFallingThroughToStaleX11Text()
    {
        var commands = new FakeCommands((name, _, _) => name == "wl-paste"
            ? new(0, [])
            : new(0, Encoding.UTF8.GetBytes("stale X11 clipboard")));
        var clipboard = new TerminalClipboard(commands, Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")), platform: ClipboardPlatform.Linux);

        Assert.Null(await clipboard.ReadTextAsync());
        Assert.Equal(["wl-paste"], commands.Invocations.Select(call => call.Command));
        Assert.Equal(["--no-newline", "--type", "text"], commands.Invocations[0].Arguments);
    }

    [Fact]
    public async Task WaylandReadFallsBackToX11WhenTheWaylandCommandFails()
    {
        var commands = new FakeCommands((name, _, _) => name switch
        {
            "wl-paste" => new(1, []),
            "xclip" => new(0, Encoding.UTF8.GetBytes("X11 text")),
            _ => new(1, [])
        });
        var clipboard = new TerminalClipboard(commands, Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")), platform: ClipboardPlatform.Linux);

        Assert.Equal("X11 text", await clipboard.ReadTextAsync());
        Assert.Equal(["wl-paste", "xclip"], commands.Invocations.Select(call => call.Command));
    }

    [Fact]
    public async Task RemoteWaylandCopyWritesClipboardAndOsc52()
    {
        var commands = new FakeCommands((_, _, _) => new(0, []));
        var controls = new List<string>();
        var clipboard = new TerminalClipboard(commands,
            Env(("WAYLAND_DISPLAY", "wayland-0"), ("SSH_CONNECTION", "client server")), controls.Add, ClipboardPlatform.Linux);

        await clipboard.CopyTextAsync("hello 😀");

        Assert.Single(commands.Invocations);
        Assert.Equal("wl-copy", commands.Invocations[0].Command);
        Assert.Equal("hello 😀", commands.Invocations[0].Input);
        Assert.Equal(["\u001b]52;c;aGVsbG8g8J+YgA==\u0007"], controls);
    }

    [Fact]
    public async Task HeadlessLinuxCopyUsesOsc52AndRejectsTextAboveTerminalLimit()
    {
        var commands = new FakeCommands((_, _, _) => null);
        var controls = new List<string>();
        var clipboard = new TerminalClipboard(commands, _ => null, controls.Add, ClipboardPlatform.Linux);

        await clipboard.CopyTextAsync("hello");
        Assert.Equal(["\u001b]52;c;aGVsbG8=\u0007"], controls);
        Assert.Empty(commands.Invocations);

        var oversized = new TerminalClipboard(commands, Env(("SSH_CLIENT", "client")), controls.Add, ClipboardPlatform.Linux);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => oversized.CopyTextAsync(new string('x', 75_001)));
        Assert.Contains("OSC 52 size limit", error.Message);
    }

    [Fact]
    public async Task RemoteDesktopCopyReportsOsc52LimitWhenNativeCopyFails()
    {
        var commands = new FakeCommands((_, _, _) => null);
        var clipboard = new TerminalClipboard(commands,
            Env(("WAYLAND_DISPLAY", "wayland-0"), ("SSH_CLIENT", "client")), _ => { }, ClipboardPlatform.Linux);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => clipboard.CopyTextAsync(new string('x', 75_001)));

        Assert.Contains("OSC 52 size limit", error.Message);
    }

    [Fact]
    public async Task MacAndWindowsUseTheirNativeTextClipboardCommands()
    {
        var macCommands = new FakeCommands((_, _, _) => new(0, Encoding.UTF8.GetBytes("mac text")));
        var mac = new TerminalClipboard(macCommands, platform: ClipboardPlatform.MacOS);
        Assert.Equal("mac text", await mac.ReadTextAsync());
        Assert.Equal("pbpaste", Assert.Single(macCommands.Invocations).Command);
        await mac.CopyTextAsync("write mac");
        Assert.Equal("pbcopy", macCommands.Invocations[1].Command);

        var windowsCommands = new FakeCommands((_, _, _) => new(0, Encoding.UTF8.GetBytes("windows text")));
        var windows = new TerminalClipboard(windowsCommands, platform: ClipboardPlatform.Windows);
        Assert.Equal("windows text", await windows.ReadTextAsync());
        Assert.Equal("powershell.exe", Assert.Single(windowsCommands.Invocations).Command);
        await windows.CopyTextAsync("write windows");
        Assert.Equal("clip", windowsCommands.Invocations[1].Command);
    }

    [Fact]
    public async Task WaylandImageReadPrefersPngAndReturnsValidatedImageBytes()
    {
        var png = CreatePng();
        var commands = new FakeCommands((name, arguments, _) => name == "wl-paste"
            ? arguments.SequenceEqual(["--list-types"])
                ? new(0, Encoding.UTF8.GetBytes("text/plain\nimage/jpeg\nimage/png\n"))
                : new(0, png)
            : new(1, []));
        var clipboard = new TerminalClipboard(commands, Env(("WAYLAND_DISPLAY", "wayland-0")), platform: ClipboardPlatform.Linux);

        var image = await clipboard.ReadImageAsync();

        Assert.Equal("image/png", image?.MimeType);
        Assert.Equal(png, image?.Bytes);
        Assert.Equal(["wl-paste", "wl-paste"], commands.Invocations.Select(call => call.Command));
        Assert.Equal(["--list-types"], commands.Invocations[0].Arguments);
        Assert.Equal(["--type", "image/png", "--no-newline"], commands.Invocations[1].Arguments);
    }

    [Fact]
    public async Task WaylandClipboardWithoutImageDoesNotUseStaleX11Image()
    {
        var commands = new FakeCommands((name, _, _) => name == "wl-paste"
            ? new(0, Encoding.UTF8.GetBytes("text/plain\n"))
            : new(0, CreatePng()));
        var clipboard = new TerminalClipboard(commands,
            Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")), platform: ClipboardPlatform.Linux);

        Assert.Null(await clipboard.ReadImageAsync());
        Assert.Equal(["wl-paste"], commands.Invocations.Select(call => call.Command));
    }

    [Fact]
    public async Task WaylandCommandFailureFallsBackToX11Image()
    {
        var png = CreatePng();
        var commands = new FakeCommands((name, arguments, _) => name switch
        {
            "wl-paste" => new(1, []),
            "xclip" when arguments.Contains("TARGETS") => new(0, Encoding.UTF8.GetBytes("text/plain\nimage/png\n")),
            "xclip" => new(0, png),
            _ => null
        });
        var clipboard = new TerminalClipboard(commands,
            Env(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")), platform: ClipboardPlatform.Linux);

        var image = await clipboard.ReadImageAsync();

        Assert.Equal("image/png", image?.MimeType);
        Assert.Equal(png, image?.Bytes);
        Assert.Equal(["wl-paste", "xclip", "xclip"], commands.Invocations.Select(call => call.Command));
    }

    [Fact]
    public async Task WslClipboardConvertsAdvertisedBmpToPng()
    {
        var commands = new FakeCommands((name, arguments, _) => name == "wl-paste"
            ? arguments.Contains("--list-types")
                ? new(0, Encoding.UTF8.GetBytes("image/bmp\n"))
                : new(0, CreateBmp())
            : null);
        var clipboard = new TerminalClipboard(commands, Env(("WSL_DISTRO_NAME", "Ubuntu")), platform: ClipboardPlatform.Linux);

        var image = await clipboard.ReadImageAsync();

        Assert.Equal("image/png", image?.MimeType);
        Assert.True(image!.Bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47 }));
    }

    [Fact]
    public async Task WslClipboardFallsBackToPowerShellAndEscapesWindowsPath()
    {
        var png = CreatePng();
        string? linuxPath = null;
        var commands = new FakeCommands((name, arguments, _) =>
        {
            switch (name)
            {
                case "wl-paste":
                case "xclip":
                    return new(1, []);
                case "wslpath":
                    linuxPath = arguments[1];
                    return new(0, Encoding.UTF8.GetBytes("C:\\Users\\O'Hare\\clip.png\n"));
                case "powershell.exe":
                    Assert.Contains("$path = 'C:\\Users\\O''Hare\\clip.png'", arguments[3]);
                    File.WriteAllBytes(linuxPath!, png);
                    return new(0, Encoding.UTF8.GetBytes("ok\n"));
                default:
                    return null;
            }
        });
        var clipboard = new TerminalClipboard(commands, Env(("WSL_DISTRO_NAME", "Ubuntu")), platform: ClipboardPlatform.Linux);

        var image = await clipboard.ReadImageAsync();

        Assert.Equal("image/png", image?.MimeType);
        Assert.Equal(png, image?.Bytes);
        Assert.Equal(["wl-paste", "xclip", "wslpath", "powershell.exe"],
            commands.Invocations.Select(call => call.Command));
    }

    [Fact]
    public async Task ClipboardImageStoreWritesPrivateImageFile()
    {
        var bytes = CreatePng();
        var path = await ClipboardImageStore.SaveAsync(new TerminalClipboardImage(bytes, "image/png"));
        try
        {
            Assert.EndsWith(".png", path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateBmp()
    {
        var bytes = new byte[58];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes((uint)bytes.Length).CopyTo(bytes, 2);
        BitConverter.GetBytes((uint)54).CopyTo(bytes, 10);
        BitConverter.GetBytes((uint)40).CopyTo(bytes, 14);
        BitConverter.GetBytes(1).CopyTo(bytes, 18);
        BitConverter.GetBytes(1).CopyTo(bytes, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((ushort)24).CopyTo(bytes, 28);
        BitConverter.GetBytes((uint)4).CopyTo(bytes, 34);
        bytes[56] = 0xff;
        return bytes;
    }

    private sealed class FakeCommands(Func<string, IReadOnlyList<string>, string?, ClipboardCommandResult?> result) : IClipboardCommandRunner
    {
        public List<Invocation> Invocations { get; } = [];

        public Task<ClipboardCommandResult?> RunAsync(string command, IReadOnlyList<string> arguments, string? input,
            int maximumOutputBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Invocations.Add(new(command, arguments.ToArray(), input));
            return Task.FromResult(result(command, arguments, input));
        }
    }

    private sealed record Invocation(string Command, IReadOnlyList<string> Arguments, string? Input);
}
