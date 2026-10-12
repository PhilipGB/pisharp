using Microsoft.Win32.SafeHandles;

namespace PiSharp.Cli.Startup;

internal sealed class CliTerminalIO : IDisposable
{
    private readonly StreamWriter? _output;
    private readonly StreamWriter? _error;
    private readonly StreamReader? _input;

    public TextReader Input => _input ?? Console.In;
    public TextWriter Output => _output ?? Console.Out;
    public TextWriter Error => _error ?? Console.Error;

    public CliTerminalIO()
    {
        try
        {
            _output = !OperatingSystem.IsWindows() && !Console.IsOutputRedirected ? OpenWriter(1) : null;
            _error = !OperatingSystem.IsWindows() && !Console.IsErrorRedirected ? OpenWriter(2) : null;
            _input = !OperatingSystem.IsWindows() && !Console.IsInputRedirected ? OpenReader(0) : null;
            if (OperatingSystem.IsLinux() && _input is not null && _output is not null)
            {
                var terminalType = Environment.GetEnvironmentVariable("TERM");
                if (!string.IsNullOrWhiteSpace(terminalType) && !string.Equals(terminalType, "dumb", StringComparison.OrdinalIgnoreCase))
                {
                    // System.Console re-enters terminfo keypad mode after signals; Pi leaves cursor and keypad modes off.
                    Environment.SetEnvironmentVariable("TERM", "dumb");
                    try { _ = Console.TreatControlCAsInput; }
                    finally { Environment.SetEnvironmentVariable("TERM", terminalType); }
                }
            }
            if (_output is not null) Console.SetOut(_output);
            if (_error is not null) Console.SetError(_error);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { _input?.Dispose(); }
        finally
        {
            try { _error?.Dispose(); }
            finally { _output?.Dispose(); }
        }
    }

    private static StreamWriter OpenWriter(int descriptor) => new(
        new FileStream(new SafeFileHandle(new IntPtr(descriptor), ownsHandle: false), FileAccess.Write, 4096),
        new System.Text.UTF8Encoding(false), 4096);

    private static StreamReader OpenReader(int descriptor) => new(
        new FileStream(new SafeFileHandle(new IntPtr(descriptor), ownsHandle: false), FileAccess.Read, 4096),
        new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
}
