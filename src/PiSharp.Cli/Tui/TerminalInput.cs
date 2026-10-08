using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Byte-level VT input; escape sequences and bracketed paste never reach the line editor as text.</summary>
public sealed class TerminalInput
{
    private const int MaxPasteBytes = 2 * 1024 * 1024;
    private readonly Stream? _input;
    private readonly bool _standardInput;
    private readonly Queue<byte> _retainedBytes = new();
    private List<byte>? _replyBytes;
    private long? _protocolDeadline;
    private int _protocolBytesRemaining;

    internal event Action<TerminalColorResponse>? TerminalColorReceived;
    internal event Action? TerminalDeviceAttributesReceived;
    internal event Action<string>? TerminalColorSchemeReceived;

    public TerminalInput(Stream input) => _input = input;
    private TerminalInput() => _standardInput = true;
    public static TerminalInput OpenConsole() => new();

    private int ReadByte()
    {
        if (_protocolDeadline is not null && (_protocolBytesRemaining <= 0 || !Available(100))) throw new TimeoutException();
        int value;
        if (_retainedBytes.TryDequeue(out var retained)) value = retained;
        else if (!_standardInput) value = _input!.ReadByte();
        else
        {
            var bytes = new byte[1];
            nint count;
            do { count = read(0, bytes, 1); } while (count < 0 && Marshal.GetLastPInvokeError() == 4);
            if (count < 0) throw new IOException("Could not read terminal input.");
            value = count == 1 ? bytes[0] : -1;
        }
        if (value >= 0)
        {
            _replyBytes?.Add((byte)value);
            if (_protocolDeadline is not null) _protocolBytesRemaining--;
        }
        return value;
    }

    // Blocking reads skip protocol traffic; polling never waits for user input after consuming it.
    public bool TryRead(int timeoutMilliseconds, out TerminalInputEvent value)
    {
        var timeout = timeoutMilliseconds;
        while (Available(timeout))
        {
            value = ReadNext();
            if (!value.IsControl) return true;
            timeout = 0;
        }
        value = new(null, null);
        return false;
    }

    public TerminalInputEvent Read()
    {
        TerminalInputEvent value;
        do { value = ReadNext(); } while (value.IsControl);
        return value;
    }

    internal void CompletePendingReplies(Func<bool> hasPendingReplies, int timeoutMilliseconds)
    {
        if (_retainedBytes.Count > 0) return;
        _protocolDeadline = Environment.TickCount64 + timeoutMilliseconds;
        _replyBytes = [];
        _protocolBytesRemaining = 64 * 1024;
        try
        {
            while (_protocolBytesRemaining > 0 && hasPendingReplies() && Available(timeoutMilliseconds))
            {
                _replyBytes.Clear();
                var next = ReadNext();
                if (next.IsControl) continue;
                RetainReplyBytes();
                break;
            }
        }
        catch (TimeoutException) { RetainReplyBytes(); }
        finally { _protocolDeadline = null; _replyBytes = null; _protocolBytesRemaining = 0; }
    }

    private void RetainReplyBytes()
    {
        // Preserve user bytes, including an incomplete key/paste, for the next input reader.
        foreach (var value in _replyBytes!) _retainedBytes.Enqueue(value);
    }

    private TerminalInputEvent ReadNext()
    {
        var first = ReadByte();
        if (first < 0) return TerminalInputEvent.EndOfStream;
        if (first == 27) return Escape();
        return ByteToEvent(first);
    }

    private TerminalInputEvent Escape()
    {
        if (!Available(40)) return Key(ConsoleKey.Escape);
        var second = ReadByte();
        if (second == ']') return ReadOsc();
        if (second is '[' or 'O')
        {
            var sequence = new StringBuilder();
            while (sequence.Length < 32 && Available(60))
            {
                var value = ReadByte();
                if (value < 0) break;
                sequence.Append((char)value);
                if (value is >= 0x40 and <= 0x7E) break;
            }
            var code = sequence.ToString();
            if (code.StartsWith('?') && code.EndsWith('u')) return new(null, null);
            if (code.StartsWith('?') && code.EndsWith('c'))
            {
                TerminalDeviceAttributesReceived?.Invoke();
                return new(null, null);
            }
            if (code is "?997;1n" or "?997;2n")
            {
                TerminalColorSchemeReceived?.Invoke(code == "?997;2n" ? "light" : "dark");
                return new(null, null);
            }
            if (code == "200~") return Paste();
            if (TryDecodeSgrMouse(code, out var mouse)) return new(null, null, mouse);
            if (TryDecodeKittyKey(code, out var kittyKey)) return kittyKey;
            if (TryDecodeModifiedKey(code, out var modifiedKey)) return Key(modifiedKey.Key, modifiedKey.KeyChar,
                modifiedKey.Modifiers.HasFlag(ConsoleModifiers.Shift), modifiedKey.Modifiers.HasFlag(ConsoleModifiers.Alt),
                modifiedKey.Modifiers.HasFlag(ConsoleModifiers.Control));
            return code switch
            {
                "A" => Key(ConsoleKey.UpArrow),
                "B" => Key(ConsoleKey.DownArrow),
                "C" => Key(ConsoleKey.RightArrow),
                "D" => Key(ConsoleKey.LeftArrow),
                "H" or "1~" or "7~" => Key(ConsoleKey.Home),
                "F" or "4~" or "8~" => Key(ConsoleKey.End),
                "5~" => Key(ConsoleKey.PageUp),
                "6~" => Key(ConsoleKey.PageDown),
                "3~" => Key(ConsoleKey.Delete),
                "Z" => Key(ConsoleKey.Tab, '\t', shift: true),
                "13;3u" or "13;3~" => Key(ConsoleKey.Enter, '\n', alt: true),
                "13;2u" or "13;2~" => Key(ConsoleKey.Enter, '\n', shift: true),
                "1;3A" => Key(ConsoleKey.UpArrow, alt: true),
                _ => new(null, null)
            };
        }
        if (second < 0) return Key(ConsoleKey.Escape);
        var plain = ByteToEvent(second);
        if (plain.Key is { } key)
            return new(new ConsoleKeyInfo(key.KeyChar, key.Key, key.Modifiers.HasFlag(ConsoleModifiers.Shift), true,
                key.Modifiers.HasFlag(ConsoleModifiers.Control)), null);
        return plain;
    }

    private static bool TryDecodeSgrMouse(string code, out TerminalMouseEvent mouse)
    {
        mouse = default;
        if (code.Length < 6 || code[0] != '<' || code[^1] is not ('M' or 'm')) return false;
        var fields = code.AsSpan(1, code.Length - 2).ToString().Split(';');
        if (fields.Length != 3 ||
            !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var button) ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var column) ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var row) ||
            button < 0 || column < 1 || row < 1)
            return false;
        mouse = new(button, column, row, code[^1] == 'm');
        return true;
    }

    private static bool TryDecodeKittyKey(string code, out TerminalInputEvent key)
    {
        key = new(null, null);
        if (!code.EndsWith('u')) return false;
        var body = code.AsSpan(0, code.Length - 1);
        var separator = body.IndexOf(';');
        var keySpec = separator < 0 ? body : body[..separator];
        var modifierSpec = separator < 0 ? "1" : body[(separator + 1)..].ToString();
        var modifierParts = modifierSpec.Split(':');
        if (modifierParts.Length is < 1 or > 2 ||
            !int.TryParse(modifierParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var modifier) || modifier < 1 ||
            modifierParts.Length == 2 &&
                (!int.TryParse(modifierParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var eventType) ||
                 eventType is < 1 or > 3))
            return false;
        if (modifierParts.Length == 2 && modifierParts[1] == "3") return true;

        var keyParts = keySpec.ToString().Split(':');
        if (keyParts.Length is < 1 or > 3 ||
            !int.TryParse(keyParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var codePoint) ||
            codePoint is < 0 or > 0x10FFFF or >= 0xD800 and <= 0xDFFF)
            return false;
        if (codePoint == 0) return true;
        var modifierBits = modifier - 1;
        if ((modifierBits & 1) != 0 && keyParts.Length > 1 && keyParts[1].Length > 0 &&
            int.TryParse(keyParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var shiftedCodePoint))
            codePoint = shiftedCodePoint;

        var shift = (modifierBits & 1) != 0;
        var alt = (modifierBits & 2) != 0;
        var control = (modifierBits & 4) != 0;
        var mapped = codePoint switch
        {
            27 => Key(ConsoleKey.Escape, shift: shift, alt: alt, control: control),
            9 => Key(ConsoleKey.Tab, '\t', shift, alt, control),
            13 => Key(ConsoleKey.Enter, '\n', shift, alt, control),
            127 => Key(ConsoleKey.Backspace, '\b', shift, alt, control),
            57348 => Key(ConsoleKey.Insert, shift: shift, alt: alt, control: control),
            57349 => Key(ConsoleKey.Delete, shift: shift, alt: alt, control: control),
            57350 => Key(ConsoleKey.LeftArrow, shift: shift, alt: alt, control: control),
            57351 => Key(ConsoleKey.RightArrow, shift: shift, alt: alt, control: control),
            57352 => Key(ConsoleKey.UpArrow, shift: shift, alt: alt, control: control),
            57353 => Key(ConsoleKey.DownArrow, shift: shift, alt: alt, control: control),
            57354 => Key(ConsoleKey.PageUp, shift: shift, alt: alt, control: control),
            57355 => Key(ConsoleKey.PageDown, shift: shift, alt: alt, control: control),
            57356 => Key(ConsoleKey.Home, shift: shift, alt: alt, control: control),
            57357 => Key(ConsoleKey.End, shift: shift, alt: alt, control: control),
            >= 57364 and <= 57387 => Key((ConsoleKey)((int)ConsoleKey.F1 + codePoint - 57364),
                shift: shift, alt: alt, control: control),
            >= 32 and < 127 => KittyAsciiKey(codePoint, shift, alt, control),
            _ => new(null, char.ConvertFromUtf32(codePoint))
        };
        key = mapped;
        return true;
    }

    private static TerminalInputEvent KittyAsciiKey(int codePoint, bool shift, bool alt, bool control)
    {
        var character = (char)codePoint;
        if (!TryMapAsciiKey(character, out var consoleKey, out var shifted))
            return new(null, character.ToString());
        if (char.IsAsciiLetter(character) && shift) character = char.ToUpperInvariant(character);
        return Key(consoleKey, control ? '\0' : character, shift: shift || shifted, alt: alt, control: control);
    }

    private static bool TryDecodeModifiedKey(string code, out ConsoleKeyInfo key)
    {
        key = default;
        if (code.EndsWith('u') && TryDecodeModifiedAscii(code, out key)) return true;
        if (code.Length < 4 || code[^1] is not ('A' or 'B' or 'C' or 'D' or 'H' or 'F' or '~')) return false;
        var separator = code.LastIndexOf(';');
        if (separator <= 0 || !int.TryParse(code.AsSpan(0, separator), out var keyCode) ||
            !int.TryParse(code.AsSpan(separator + 1, code.Length - separator - 2), out var modifierCode) || modifierCode < 2)
            return false;
        var modifierBits = modifierCode - 1;
        if ((modifierBits & ~7) != 0) return false;
        var consoleKey = code[^1] switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            '~' when keyCode is 1 or 7 => ConsoleKey.Home,
            '~' when keyCode == 2 => ConsoleKey.Insert,
            '~' when keyCode == 3 => ConsoleKey.Delete,
            '~' when keyCode is 4 or 8 => ConsoleKey.End,
            '~' when keyCode == 5 => ConsoleKey.PageUp,
            '~' when keyCode == 6 => ConsoleKey.PageDown,
            _ => ConsoleKey.NoName
        };
        if (consoleKey == ConsoleKey.NoName) return false;
        key = new ConsoleKeyInfo('\0', consoleKey, (modifierBits & 1) != 0,
            (modifierBits & 2) != 0, (modifierBits & 4) != 0);
        return true;
    }

    private static bool TryDecodeModifiedAscii(string code, out ConsoleKeyInfo key)
    {
        key = default;
        var separator = code.IndexOf(';');
        if (separator <= 0 || !int.TryParse(code.AsSpan(0, separator), out var keyCode) || keyCode is < 32 or > 126 ||
            !int.TryParse(code.AsSpan(separator + 1, code.Length - separator - 2), out var modifierCode) || modifierCode < 2)
            return false;
        var modifierBits = modifierCode - 1;
        if ((modifierBits & ~7) != 0 || !TryMapAsciiKey((char)keyCode, out var consoleKey, out var shifted)) return false;
        key = new ConsoleKeyInfo('\0', consoleKey, shifted || (modifierBits & 1) != 0,
            (modifierBits & 2) != 0, (modifierBits & 4) != 0);
        return true;
    }

    // OSC replies end in BEL or ST (ESC \\). Only validated color reports escape the input parser.
    private TerminalInputEvent ReadOsc()
    {
        var payload = new StringBuilder();
        var escape = false;
        for (var i = 0; i < 4096 && Available(60); i++)
        {
            var value = ReadByte();
            if (value < 0) return TerminalInputEvent.EndOfStream;
            if (value == 7 || (escape && value == '\\'))
            {
                if (escape && payload.Length > 0 && payload[^1] == '\u001b') payload.Length--;
                if (TerminalColorResponse.TryParse(payload.ToString(), out var color)) TerminalColorReceived?.Invoke(color);
                return new(null, null);
            }
            payload.Append((char)value);
            escape = value == 27;
        }
        if (Available(0)) throw new InvalidDataException("Terminal OSC reply exceeds 4096 bytes.");
        // A truncated reply is not user input; discard it rather than terminating the editor.
        return new(null, null);
    }

    private TerminalInputEvent Paste()
    {
        var bytes = new List<byte>();
        var end = new byte[] { 27, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~' };
        var matched = 0;
        while (bytes.Count < MaxPasteBytes)
        {
            var value = ReadByte();
            if (value < 0) return TerminalInputEvent.EndOfStream;
            if (value == end[matched])
            {
                if (++matched == end.Length)
                    return new(null, Encoding.UTF8.GetString(bytes.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
                continue;
            }
            if (matched > 0)
            {
                bytes.AddRange(end.AsSpan(0, matched).ToArray());
                matched = 0;
                if (value == end[0]) { matched = 1; continue; }
            }
            bytes.Add((byte)value);
        }
        throw new InvalidDataException("Bracketed paste exceeds 2MB.");
    }

    private TerminalInputEvent ByteToEvent(int value)
    {
        if (value is 10 or 13) return Key(ConsoleKey.Enter, '\n');
        if (value is 8 or 127) return Key(ConsoleKey.Backspace, '\b');
        if (value == 9) return Key(ConsoleKey.Tab, '\t');
        if (value < 32) return Key((ConsoleKey)(ConsoleKey.A + value - 1), (char)value, control: true);
        if (value < 128)
        {
            var character = (char)value;
            return TryMapAsciiKey(character, out var key, out var shift)
                ? Key(key, character, shift: shift)
                : Key(ConsoleKey.NoName, character);
        }
        var length = value < 0xE0 ? 2 : value < 0xF0 ? 3 : 4;
        var bytes = new byte[length];
        bytes[0] = (byte)value;
        for (var i = 1; i < length; i++)
        {
            var next = ReadByte();
            if (next < 0) return TerminalInputEvent.EndOfStream;
            bytes[i] = (byte)next;
        }
        return new(null, Encoding.UTF8.GetString(bytes));
    }

    private static bool TryMapAsciiKey(char character, out ConsoleKey key, out bool shift)
    {
        shift = false;
        if (char.IsAsciiLetter(character))
        {
            key = (ConsoleKey)char.ToUpperInvariant(character);
            shift = char.IsUpper(character);
            return true;
        }
        if (char.IsAsciiDigit(character))
        {
            key = (ConsoleKey)((int)ConsoleKey.D0 + character - '0');
            return true;
        }
        (key, shift) = character switch
        {
            ' ' => (ConsoleKey.Spacebar, false),
            '`' => (ConsoleKey.Oem3, false),
            '~' => (ConsoleKey.Oem3, true),
            '-' => (ConsoleKey.OemMinus, false),
            '_' => (ConsoleKey.OemMinus, true),
            '=' => (ConsoleKey.OemPlus, false),
            '+' => (ConsoleKey.OemPlus, true),
            '[' => (ConsoleKey.Oem4, false),
            '{' => (ConsoleKey.Oem4, true),
            ']' => (ConsoleKey.Oem6, false),
            '}' => (ConsoleKey.Oem6, true),
            '\\' => (ConsoleKey.Oem5, false),
            '|' => (ConsoleKey.Oem5, true),
            ';' => (ConsoleKey.Oem1, false),
            ':' => (ConsoleKey.Oem1, true),
            '\'' => (ConsoleKey.Oem7, false),
            ',' => (ConsoleKey.OemComma, false),
            '<' => (ConsoleKey.OemComma, true),
            '.' => (ConsoleKey.OemPeriod, false),
            '>' => (ConsoleKey.OemPeriod, true),
            '/' => (ConsoleKey.Oem2, false),
            '?' => (ConsoleKey.Oem2, true),
            '!' => (ConsoleKey.D1, true),
            '@' => (ConsoleKey.D2, true),
            '#' => (ConsoleKey.D3, true),
            '$' => (ConsoleKey.D4, true),
            '%' => (ConsoleKey.D5, true),
            '^' => (ConsoleKey.D6, true),
            '&' => (ConsoleKey.D7, true),
            '*' => (ConsoleKey.D8, true),
            '(' => (ConsoleKey.D9, true),
            ')' => (ConsoleKey.D0, true),
            _ => (ConsoleKey.NoName, false)
        };
        return key != ConsoleKey.NoName;
    }

    private bool Available(int milliseconds)
    {
        if (_protocolDeadline is { } deadline)
        {
            var remaining = deadline - Environment.TickCount64;
            // The deadline bounds waiting; queued replies still belong to this reader.
            milliseconds = (int)Math.Min(milliseconds, Math.Max(0, remaining));
        }
        if (_retainedBytes.Count > 0) return true;
        if (!_standardInput && _input!.CanSeek) return _input.Position < _input.Length;
        if (!OperatingSystem.IsLinux()) return false;
        var descriptors = new[] { new PollFd { FileDescriptor = 0, Events = 1 } };
        return poll(descriptors, 1, milliseconds) > 0 && (descriptors[0].ReturnedEvents & (1 | 16)) != 0;
    }

    private static TerminalInputEvent Key(ConsoleKey key, char character = '\0', bool shift = false, bool alt = false, bool control = false) =>
        new(new ConsoleKeyInfo(character, key, shift, alt, control), null);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public int FileDescriptor; public short Events; public short ReturnedEvents; }

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] descriptors, uint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, [Out] byte[] buffer, nuint count);
}

public sealed record TerminalInputEvent(ConsoleKeyInfo? Key, string? Text, TerminalMouseEvent? Mouse = null,
    bool IsEndOfStream = false)
{
    public static TerminalInputEvent EndOfStream { get; } = new(null, null, IsEndOfStream: true);
    internal bool IsControl => !IsEndOfStream && Key is null && Text is null && Mouse is null;
}

internal static class TerminalKeyboardMode
{
    public const string Enable = "\u001b[>7u\u001b[?u";
    public const string Disable = "\u001b[<u";
}

/// <summary>SGR mouse report coordinates are one-based terminal columns and rows.</summary>
public readonly record struct TerminalMouseEvent(int Button, int Column, int Row, bool IsRelease)
{
    public bool IsMotion => (Button & 32) != 0;
    public bool IsWheel => (Button & 64) != 0;

    /// <summary>Positive values move the transcript toward earlier rows; horizontal wheel events are ignored.</summary>
    public int WheelScrollDelta
    {
        get
        {
            if (!IsWheel) return 0;
            var direction = Button & 3;
            if (direction is not (0 or 1)) return 0;
            var lines = (Button & 8) != 0 ? 5 : 1;
            return direction == 0 ? lines : -lines;
        }
    }
}

/// <summary>Restores tty state after exceptions, EOF and normal exit. Only used with an attached terminal.</summary>
internal sealed class TerminalMode : IDisposable
{
    private static readonly AsyncLocal<int> s_depth = new();
    private static readonly AsyncLocal<TerminalMode?> s_owner = new();
    private readonly string _original;
    private readonly TerminalScreen? _screen;
    private readonly bool _ownsMode;
    private readonly TerminalMode? _owner;
    private bool _suspended;
    private bool _disposed;
    private TerminalMode(string original, TerminalScreen? screen, bool ownsMode, TerminalMode? owner = null)
    {
        _original = original;
        _screen = screen;
        _ownsMode = ownsMode;
        _owner = owner;
    }

    public static TerminalMode Enter(TerminalScreen? screen = null)
    {
        if (s_depth.Value > 0)
        {
            s_depth.Value++;
            return new TerminalMode("", screen, ownsMode: false, owner: s_owner.Value);
        }
        var state = Stty("-g").Trim();
        if (string.IsNullOrEmpty(state)) throw new IOException("Could not read terminal settings.");
        Stty("-icanon", "-echo", "-isig", "min", "1", "time", "0");
        try { WriteControl(screen, "\u001b[?2004h"); }
        catch { Stty(state); throw; }
        var owner = new TerminalMode(state, screen, ownsMode: true);
        s_owner.Value = owner;
        s_depth.Value = 1;
        return owner;
    }

    public void Dispose()
    {
        if (!_ownsMode)
        {
            if (_disposed) return;
            s_depth.Value = Math.Max(0, s_depth.Value - 1);
            _disposed = true;
            return;
        }
        if (_disposed) return;
        try { WriteControl(_screen, "\u001b[?2004l"); }
        finally
        {
            try { if (!_suspended) Stty(_original); }
            finally
            {
                s_depth.Value = 0;
                s_owner.Value = null;
                _disposed = true;
            }
        }
    }

    public void Suspend()
    {
        if (_disposed || _suspended) return;
        if (!_ownsMode)
        {
            if (_owner is null || _owner._disposed) return;
            _owner.SuspendOwned();
            _suspended = true;
            return;
        }
        SuspendOwned();
    }

    private void SuspendOwned()
    {
        if (!_ownsMode || _disposed || _suspended) return;
        WriteControl(_screen, "\u001b[?2004l");
        try { Stty(_original); }
        catch
        {
            WriteControl(_screen, "\u001b[?2004h");
            throw;
        }
        _suspended = true;
    }

    public void Resume()
    {
        if (_disposed || !_suspended) return;
        if (!_ownsMode)
        {
            _owner?.ResumeOwned();
            _suspended = false;
            return;
        }
        ResumeOwned();
    }

    private void ResumeOwned()
    {
        if (!_ownsMode || _disposed || !_suspended) return;
        Stty(_original);
        Stty("-icanon", "-echo", "-isig", "min", "1", "time", "0");
        try { WriteControl(_screen, "\u001b[?2004h"); }
        catch
        {
            Stty(_original);
            throw;
        }
        _suspended = false;
    }

    private static void WriteControl(TerminalScreen? screen, string value)
    {
        if (screen is { IsActive: true }) screen.WriteControl(value);
        else Console.Write(value);
    }

    private static string Stty(params string[] args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("/bin/stty") { RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"stty: {error.Trim()}");
        return output;
    }
}
