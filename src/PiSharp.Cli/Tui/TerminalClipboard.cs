using System.Text;

namespace PiSharp.Cli.Tui;

internal enum ClipboardPlatform { Windows, MacOS, Linux }

/// <summary>Text clipboard access through native platform helpers and terminal OSC 52 fallback.</summary>
internal sealed class TerminalClipboard
{
    private const int MaximumTextBytes = 16 * 1024 * 1024;
    private const int MaximumOsc52EncodedLength = 100_000;
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

    private bool IsRemoteSession() => Has("SSH_CONNECTION") || Has("SSH_CLIENT") || Has("MOSH_CONNECTION");

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
