using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PiSharp.Cli.Tui;

/// <summary>Byte-level VT input; escape sequences and bracketed paste never reach the line editor as text.</summary>
public sealed class TerminalInput
{
    private const int MaxPasteBytes = 2 * 1024 * 1024;
    private readonly Stream? _input;
    private readonly bool _standardInput;

    public TerminalInput(Stream input) => _input = input;
    private TerminalInput() => _standardInput = true;
    public static TerminalInput OpenConsole() => new();

    private int ReadByte()
    {
        if (!_standardInput) return _input!.ReadByte();
        var bytes = new byte[1];
        return read(0, bytes, 1) == 1 ? bytes[0] : -1;
    }

    public bool TryRead(int timeoutMilliseconds, out TerminalInputEvent value)
    {
        if (!Available(timeoutMilliseconds))
        {
            value = new(null, null);
            return false;
        }
        value = Read();
        return true;
    }

    public TerminalInputEvent Read()
    {
        var first = ReadByte();
        if (first < 0) return new(null, null);
        if (first == 27) return Escape();
        return ByteToEvent(first);
    }

    private TerminalInputEvent Escape()
    {
        if (!Available(40)) return Key(ConsoleKey.Escape);
        var second = ReadByte();
        if (second == ']') return SkipOsc();
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
            if (code == "200~") return Paste();
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

    // OSC terminal replies (including color queries 10/11) end in BEL or ST (ESC \\).
    // They are not keystrokes; never insert their payload into the editable draft.
    private TerminalInputEvent SkipOsc()
    {
        var escape = false;
        for (var i = 0; i < 4096 && Available(60); i++)
        {
            var value = ReadByte();
            if (value < 0 || value == 7 || (escape && value == '\\')) return new(null, null);
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
            if (value < 0) return new(null, null);
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
            if (next < 0) return new(null, null);
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
        if (!_standardInput && _input!.CanSeek) return _input.Position < _input.Length;
        if (!OperatingSystem.IsLinux()) return false;
        var descriptors = new[] { new PollFd { FileDescriptor = 0, Events = 1 } };
        return poll(descriptors, 1, milliseconds) > 0 && (descriptors[0].ReturnedEvents & 1) != 0;
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

public sealed record TerminalInputEvent(ConsoleKeyInfo? Key, string? Text);

/// <summary>Restores tty state after exceptions, EOF and normal exit. Only used with an attached terminal.</summary>
internal sealed class TerminalMode : IDisposable
{
    private readonly string _original;
    private readonly TerminalScreen? _screen;
    private TerminalMode(string original, TerminalScreen? screen) { _original = original; _screen = screen; }

    public static TerminalMode Enter(TerminalScreen? screen = null)
    {
        var state = Stty("-g").Trim();
        if (string.IsNullOrEmpty(state)) throw new IOException("Could not read terminal settings.");
        Stty("-icanon", "-echo", "-isig", "min", "1", "time", "0");
        try { WriteControl(screen, "\u001b[?2004h"); }
        catch { Stty(state); throw; }
        return new TerminalMode(state, screen);
    }

    public void Dispose()
    {
        WriteControl(_screen, "\u001b[?2004l");
        Stty(_original);
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
