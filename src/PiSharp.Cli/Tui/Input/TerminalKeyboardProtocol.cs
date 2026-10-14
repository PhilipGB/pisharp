namespace PiSharp.Cli.Tui;

internal static class TerminalKeyboardMode
{
    public const string Enable = "\u001b[>7u\u001b[?u\u001b[c";
    public const string Disable = "\u001b[<u";
}

internal sealed class TerminalKeyboardProtocol
{
    private int _pendingDeviceAttributes;
    private bool _pushed;
    private bool _kitty;
    private bool _fallback;

    internal string Begin()
    {
        _pendingDeviceAttributes++;
        _pushed = true;
        _kitty = false;
        _fallback = false;
        return TerminalKeyboardMode.Enable;
    }

    internal string End()
    {
        if (!_pushed) return "";
        var control = TerminalKeyboardMode.Disable + (_fallback ? "\u001b[>4;0m" : "");
        _pushed = _kitty = _fallback = false;
        return control;
    }

    internal string HandleFlags(int flags)
    {
        if (!_pushed) return "";
        if (flags == 0) return EnableFallback();
        _kitty = true;
        if (!_fallback) return "";
        _fallback = false;
        return "\u001b[>4;0m";
    }

    internal (bool Consumed, string Control) HandleDeviceAttributes()
    {
        if (_pendingDeviceAttributes == 0) return (false, "");
        _pendingDeviceAttributes--;
        return (true, EnableFallback());
    }

    private string EnableFallback()
    {
        if (!_pushed || _kitty || _fallback) return "";
        _fallback = true;
        return "\u001b[>4;2m";
    }
}

internal sealed class TerminalProtocolInputBinding(Action<TerminalColorResponse> colors, Action deviceAttributes,
    Action<string> appearance, Action<int> keyboardFlags) : IDisposable
{
    private TerminalInput? _input;

    internal void Attach(TerminalInput input)
    {
        if (ReferenceEquals(input, _input)) return;
        Dispose();
        _input = input;
        input.TerminalColorReceived += colors;
        input.TerminalDeviceAttributesReceived += deviceAttributes;
        input.TerminalColorSchemeReceived += appearance;
        input.TerminalKeyboardFlagsReceived += keyboardFlags;
    }

    public void Dispose()
    {
        if (_input is not { } input) return;
        input.TerminalColorReceived -= colors;
        input.TerminalDeviceAttributesReceived -= deviceAttributes;
        input.TerminalColorSchemeReceived -= appearance;
        input.TerminalKeyboardFlagsReceived -= keyboardFlags;
        _input = null;
    }
}
