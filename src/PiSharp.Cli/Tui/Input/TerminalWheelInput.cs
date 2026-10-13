namespace PiSharp.Cli.Tui;

/// <summary>Routes wheel events left unhandled by a modal component to the transcript.</summary>
internal static class TerminalWheelInput
{
    internal static bool TryRouteUnhandled(TerminalScreen screen, TerminalInputEvent input)
    {
        if (input.Mouse is not { IsWheel: true } mouse) return false;
        screen.HandleMouse(mouse);
        return true;
    }
}
