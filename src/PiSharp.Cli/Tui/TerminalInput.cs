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
            return code switch
            {
                "A" => Key(ConsoleKey.UpArrow),
                "B" => Key(ConsoleKey.DownArrow),
                "C" => Key(ConsoleKey.RightArrow),
                "D" => Key(ConsoleKey.LeftArrow),
                "H" or "1~" or "7~" => Key(ConsoleKey.Home),
                "F" or "4~" or "8~" => Key(ConsoleKey.End),
                "3~" => Key(ConsoleKey.Delete),
                "Z" => Key(ConsoleKey.Tab, '\t', shift: true),
                "13;3u" or "13;3~" => Key(ConsoleKey.Enter, '\n', alt: true),
                "13;2u" or "13;2~" => Key(ConsoleKey.Enter, '\n', shift: true),
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
        if (value < 128) return Key(char.IsLetter((char)value) ? (ConsoleKey)char.ToUpperInvariant((char)value) : ConsoleKey.NoName, (char)value);
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
    private TerminalMode(string original) { _original = original; }

    public static TerminalMode Enter()
    {
        var state = Stty("-g").Trim();
        if (string.IsNullOrEmpty(state)) throw new IOException("Could not read terminal settings.");
        Stty("-icanon", "-echo", "-isig", "min", "1", "time", "0");
        try { Console.Write("\u001b[?2004h"); }
        catch { Stty(state); throw; }
        return new TerminalMode(state);
    }

    public void Dispose()
    {
        Console.Write("\u001b[?2004l");
        Stty(_original);
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
