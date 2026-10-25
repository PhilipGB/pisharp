using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Tools;

namespace PiSharp.Cli.Tui;

internal enum ClipboardPlatform { Windows, MacOS, Linux }
internal sealed record TerminalClipboardImage(byte[] Bytes, string MimeType);

/// <summary>Clipboard text and bounded image access through native platform helpers.</summary>
internal sealed class TerminalClipboard
{
    private const int MaximumTextBytes = 16 * 1024 * 1024;
    private const int MaximumOsc52EncodedLength = 100_000;
    private const int MaximumImageBytes = 20 * 1024 * 1024;
    private const int MaximumClipboardTypeBytes = 16 * 1024;
    private const int MaximumClipboardPathBytes = 1024 * 1024;
    private static readonly string[] s_preferredImageTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];
    internal const string MacFilePathsScript = """
        use framework "AppKit"
        use framework "Foundation"
        use scripting additions
        set pasteboard to current application's NSPasteboard's generalPasteboard()
        set urlClasses to current application's NSArray's arrayWithObject:(current application's NSURL)
        set options to current application's NSDictionary's dictionaryWithObject:(current application's NSNumber's numberWithBool:true) forKey:(current application's NSPasteboardURLReadingFileURLsOnlyKey)
        set urls to pasteboard's readObjectsForClasses:urlClasses options:options
        set paths to current application's NSMutableArray's array()
        if urls is not missing value then
            repeat with fileUrl in urls
                paths's addObject:(fileUrl's |path|())
            end repeat
        end if
        set jsonData to current application's NSJSONSerialization's dataWithJSONObject:paths options:0 |error|:(missing value)
        set jsonText to current application's NSString's alloc()'s initWithData:jsonData encoding:(current application's NSUTF8StringEncoding)
        return jsonText as text
        """;
    internal const string MacImageScript = "on run argv\ntry\nset clipboardTypes to the clipboard info\nif clipboardTypes does not contain «class PNGf» and clipboardTypes does not contain «class TIFF» then return \"empty\"\nset imageData to the clipboard as «class PNGf»\nif (count of imageData) > 20971520 then return \"large\"\nset imagePath to item 1 of argv\nset imageFile to open for access (POSIX file imagePath) with write permission\nset eof of imageFile to 0\nwrite imageData to imageFile\nclose access imageFile\nreturn \"ok\"\non error errorMessage number errorNumber\ntry\nclose access imageFile\nend try\nreturn \"error:\" & errorNumber & \":\" & errorMessage\nend try\nend run";
    private readonly IClipboardCommandRunner _commands;
    private readonly Func<string, string?> _environment;
    private readonly Action<string> _writeTerminalControl;
    private readonly ClipboardPlatform _platform;

    public TerminalClipboard(IClipboardCommandRunner? commands = null,
        Func<string, string?>? environment = null,
        Action<string>? writeTerminalControl = null,
        ClipboardPlatform? platform = null)
    {
        _commands = commands ?? new ClipboardCommandRunner();
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _writeTerminalControl = writeTerminalControl ?? Console.Write;
        _platform = platform ?? CurrentPlatform();
    }

    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        if (_platform == ClipboardPlatform.Linux)
        {
            if (Has("TERMUX_VERSION") && await ReadCommandAsync("termux-clipboard-get", [], cancellationToken) is { } termux)
                return DecodeText(termux);
            if (Has("WAYLAND_DISPLAY") && await ReadCommandAsync("wl-paste", ["--no-newline", "--type", "text"], cancellationToken) is { } wayland)
                return DecodeText(wayland);
            if (Has("DISPLAY"))
            {
                if (await ReadCommandAsync("xclip", ["-selection", "clipboard", "-out"], cancellationToken) is { } xclip)
                    return DecodeText(xclip);
                if (await ReadCommandAsync("xsel", ["--clipboard", "--output"], cancellationToken) is { } xsel)
                    return DecodeText(xsel);
            }
            return null;
        }

        if (_platform == ClipboardPlatform.MacOS)
        {
            var result = await ReadCommandAsync("pbpaste", [], cancellationToken);
            return result is null ? null : DecodeText(result);
        }

        var windows = await ReadCommandAsync("powershell.exe",
            ["-NoProfile", "-Command", "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::Out.Write((Get-Clipboard -Raw))"],
            cancellationToken);
        return windows is null ? null : DecodeText(windows);
    }

    public async Task<TerminalClipboardImage?> ReadImageAsync(CancellationToken cancellationToken = default)
    {
        TerminalClipboardImage? image;
        if (_platform == ClipboardPlatform.Linux)
        {
            if (Has("TERMUX_VERSION")) return null;
            var isWsl = IsWsl();
            var wayland = Has("WAYLAND_DISPLAY") ||
                string.Equals(_environment("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase) || isWsl;
            var result = wayland ? await ReadWaylandImageAsync(cancellationToken) : (Available: false, Image: (TerminalClipboardImage?)null);
            if (!result.Available)
                result = await ReadX11ImageAsync(cancellationToken);
            image = result.Image;
            if (image is null && isWsl)
                image = await ReadPowerShellImageAsync(wsl: true, cancellationToken);
        }
        else if (_platform == ClipboardPlatform.MacOS)
        {
            image = await ReadMacImageAsync(cancellationToken);
        }
        else
        {
            image = await ReadPowerShellImageAsync(wsl: false, cancellationToken);
        }

        if (image is null || image.Bytes.Length == 0 || image.Bytes.Length > MaximumImageBytes) return null;
        var processed = ReadImageProcessor.Process(image.Bytes, image.MimeType, cancellationToken,
            autoResizeImages: false);
        if (processed.ImageMimeType is not { } mimeType || processed.ImageDataBase64 is not { Length: > 0 } encoded)
            return null;
        return new(Convert.FromBase64String(encoded), mimeType);
    }

    public async Task<string[]?> ReadFilePathsAsync(CancellationToken cancellationToken = default)
    {
        if (_platform != ClipboardPlatform.MacOS) return null;
        var result = await _commands.RunAsync("osascript", ["-e", MacFilePathsScript], input: null,
            maximumOutputBytes: MaximumClipboardPathBytes, timeout: TimeSpan.FromSeconds(5), cancellationToken);
        if (result is not { ExitCode: 0 })
            throw new IOException("Could not read file paths from the macOS clipboard.");

        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(result.Output);
            if (paths is null || paths.Length == 0) return null;
            if (paths.Length > 4096 || paths.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Clipboard returned invalid file paths.");
            return paths;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Clipboard returned invalid file paths.", error);
        }
    }

    public async Task CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
            throw new InvalidOperationException("Clipboard text exceeds the 16 MiB limit.");

        var copied = _platform switch
        {
            ClipboardPlatform.Windows => await TryWriteAsync("clip", [], text, cancellationToken),
            ClipboardPlatform.MacOS => await TryWriteAsync("pbcopy", [], text, cancellationToken),
            _ => await CopyOnLinuxAsync(text, cancellationToken)
        };

        var remote = IsRemoteSession();
        var headlessLinux = _platform == ClipboardPlatform.Linux &&
            !Has("DISPLAY") && !Has("WAYLAND_DISPLAY") && !Has("TERMUX_VERSION");
        var osc52Written = false;
        var osc52Required = remote || !copied && headlessLinux;
        if (osc52Required)
            osc52Written = WriteOsc52(text);

        if (copied || osc52Written) return;
        if (osc52Required)
            throw new InvalidOperationException("Clipboard unavailable: text exceeds the OSC 52 size limit.");
        throw new InvalidOperationException(ClipboardFailureMessage());
    }

    private async Task<bool> CopyOnLinuxAsync(string text, CancellationToken cancellationToken)
    {
        if (Has("TERMUX_VERSION") && await TryWriteAsync("termux-clipboard-set", [], text, cancellationToken)) return true;
        if (Has("WAYLAND_DISPLAY") && await TryWriteAsync("wl-copy", [], text, cancellationToken)) return true;
        if (Has("DISPLAY"))
        {
            if (await TryWriteAsync("xclip", ["-selection", "clipboard"], text, cancellationToken)) return true;
            if (await TryWriteAsync("xsel", ["--clipboard", "--input"], text, cancellationToken)) return true;
        }
        return false;
    }

    private async Task<byte[]?> ReadCommandAsync(string command, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _commands.RunAsync(command, arguments, input: null, MaximumTextBytes,
            TimeSpan.FromSeconds(5), cancellationToken);
        return result is { ExitCode: 0 } ? result.Output : null;
    }

    private async Task<(bool Available, TerminalClipboardImage? Image)> ReadWaylandImageAsync(CancellationToken cancellationToken)
    {
        var types = await _commands.RunAsync("wl-paste", ["--list-types"], input: null,
            MaximumClipboardTypeBytes, TimeSpan.FromSeconds(1), cancellationToken);
        if (types is not { ExitCode: 0 }) return (false, null);
        var selectedType = SelectImageMimeType(types.Output);
        if (selectedType is null) return (true, null);
        var data = await _commands.RunAsync("wl-paste", ["--type", selectedType, "--no-newline"], input: null,
            MaximumImageBytes, TimeSpan.FromSeconds(5), cancellationToken);
        if (data is not { ExitCode: 0 }) return (false, null);
        return (true, data.Output.Length == 0 ? null : new(data.Output, BaseMimeType(selectedType)));
    }

    private async Task<(bool Available, TerminalClipboardImage? Image)> ReadX11ImageAsync(CancellationToken cancellationToken)
    {
        var targets = await _commands.RunAsync("xclip", ["-selection", "clipboard", "-t", "TARGETS", "-o"],
            input: null, MaximumClipboardTypeBytes, TimeSpan.FromSeconds(1), cancellationToken);
        if (targets is not { ExitCode: 0 }) return (false, null);
        var selectedType = SelectImageMimeType(targets.Output);
        if (selectedType is null) return (true, null);
        var data = await _commands.RunAsync("xclip", ["-selection", "clipboard", "-t", selectedType, "-o"],
            input: null, MaximumImageBytes, TimeSpan.FromSeconds(5), cancellationToken);
        if (data is not { ExitCode: 0 }) return (false, null);
        return (true, data.Output.Length == 0 ? null : new(data.Output, BaseMimeType(selectedType)));
    }

    private async Task<TerminalClipboardImage?> ReadMacImageAsync(CancellationToken cancellationToken)
    {
        var path = NewClipboardImagePath(".png");
        try
        {
            var result = await _commands.RunAsync("osascript", ["-e", MacImageScript, path], input: null,
                maximumOutputBytes: 1024, timeout: TimeSpan.FromSeconds(5), cancellationToken);
            if (result is not { ExitCode: 0 })
                throw new IOException("Could not read an image from the macOS clipboard.");

            var status = DecodeText(result.Output)?.Trim();
            return status switch
            {
                "empty" => null,
                "large" => throw new InvalidDataException("Clipboard image exceeds the 20 MiB limit."),
                "ok" => await ReadTemporaryImageAsync(path, "image/png", cancellationToken),
                _ => throw new IOException($"macOS clipboard image read failed: {status ?? "no status"}.")
            };
        }
        finally { TryDelete(path); }
    }

    private async Task<TerminalClipboardImage?> ReadPowerShellImageAsync(bool wsl, CancellationToken cancellationToken)
    {
        var path = NewClipboardImagePath(".png");
        try
        {
            var commandPath = path;
            if (wsl)
            {
                var windowsPath = await _commands.RunAsync("wslpath", ["-w", path], input: null,
                    maximumOutputBytes: 4096, timeout: TimeSpan.FromSeconds(1), cancellationToken);
                if (windowsPath is not { ExitCode: 0 } || DecodeText(windowsPath.Output)?.Trim() is not { Length: > 0 } converted)
                    return null;
                commandPath = converted;
            }

            var escapedPath = commandPath.Replace("'", "''", StringComparison.Ordinal);
            var script = "Add-Type -AssemblyName System.Windows.Forms; Add-Type -AssemblyName System.Drawing; " +
                $"$path = '{escapedPath}'; $ErrorActionPreference = 'Stop'; " +
                "try { $img = [System.Windows.Forms.Clipboard]::GetImage(); " +
                "if (-not $img) { 'empty' } " +
                "elseif (($img.Width * $img.Height) -gt 32000000) { 'large' } " +
                "else { $img.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); " +
                "if ((Get-Item $path).Length -le 20971520) { 'ok' } else { Remove-Item -Force $path; 'large' } } " +
                "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
            var result = await _commands.RunAsync("powershell.exe", ["-NoProfile", "-STA", "-Command", script], input: null,
                maximumOutputBytes: 1024, timeout: TimeSpan.FromSeconds(5), cancellationToken);
            if (result is not { ExitCode: 0 })
            {
                if (wsl) return null;
                throw new IOException("Could not read an image from the Windows clipboard.");
            }

            var status = DecodeText(result.Output)?.Trim();
            return status switch
            {
                "empty" => null,
                "large" => throw new InvalidDataException("Clipboard image exceeds the 20 MiB limit."),
                "ok" => await ReadTemporaryImageAsync(path, "image/png", cancellationToken),
                _ when wsl => null,
                _ => throw new IOException($"Windows clipboard image read failed: {status ?? "no status"}.")
            };
        }
        finally { TryDelete(path); }
    }

    private async Task<TerminalClipboardImage?> ReadTemporaryImageAsync(string path, string mimeType,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0)
            throw new InvalidDataException("Clipboard image data is empty.");
        if (stream.Length > MaximumImageBytes)
            throw new InvalidDataException("Clipboard image exceeds the 20 MiB limit.");
        using var image = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var remaining = MaximumImageBytes + 1L - image.Length;
            if (remaining <= 0)
                throw new InvalidDataException("Clipboard image exceeds the 20 MiB limit.");
            var count = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
            if (read == 0) break;
            await image.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (image.Length > MaximumImageBytes)
                throw new InvalidDataException("Clipboard image exceeds the 20 MiB limit.");
        }
        return new(image.ToArray(), mimeType);
    }

    private bool IsWsl()
    {
        if (Has("WSL_DISTRO_NAME") || Has("WSLENV")) return true;
        try
        {
            var version = File.ReadAllText("/proc/version");
            return version.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
                version.Contains("wsl", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string? SelectImageMimeType(byte[] bytes)
    {
        var types = Encoding.UTF8.GetString(bytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        foreach (var preferred in s_preferredImageTypes)
            if (types.FirstOrDefault(type => BaseMimeType(type).Equals(preferred, StringComparison.OrdinalIgnoreCase)) is { } match)
                return match;
        return types.FirstOrDefault(type => BaseMimeType(type).StartsWith("image/", StringComparison.OrdinalIgnoreCase));
    }

    private static string BaseMimeType(string value) => value.Split(';', 2)[0].Trim().ToLowerInvariant();

    private static string NewClipboardImagePath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"pisharp-clipboard-{Guid.NewGuid():N}{extension}");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task<bool> TryWriteAsync(string command, IReadOnlyList<string> arguments, string text,
        CancellationToken cancellationToken)
    {
        var result = await _commands.RunAsync(command, arguments, text, MaximumTextBytes,
            TimeSpan.FromSeconds(5), cancellationToken);
        return result is { ExitCode: 0 };
    }

    private bool WriteOsc52(string text)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        if (encoded.Length > MaximumOsc52EncodedLength) return false;
        _writeTerminalControl($"\u001b]52;c;{encoded}\u0007");
        return true;
    }

    private bool Has(string name) => !string.IsNullOrWhiteSpace(_environment(name));

    private bool IsRemoteSession() => Has("SSH_CONNECTION") || Has("SSH_CLIENT") || Has("MOSH_CONNECTION") ||
        _environment("PI_CLIPBOARD_OSC52") == "1";

    private string ClipboardFailureMessage()
    {
        if (_platform == ClipboardPlatform.Linux)
        {
            if (Has("TERMUX_VERSION")) return "Clipboard unavailable: install the Termux:API app and termux-api package.";
            if (Has("WAYLAND_DISPLAY")) return "Clipboard unavailable: install wl-clipboard (wl-copy) or check Wayland access.";
            if (Has("DISPLAY")) return "Clipboard unavailable: install xclip or xsel, or check X11 access.";
        }
        return "Clipboard unavailable.";
    }

    private static string? DecodeText(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length == 0 ? null : text;
    }

    private static ClipboardPlatform CurrentPlatform() => OperatingSystem.IsWindows()
        ? ClipboardPlatform.Windows
        : OperatingSystem.IsMacOS() ? ClipboardPlatform.MacOS : ClipboardPlatform.Linux;
}
