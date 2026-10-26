using System.Runtime.InteropServices;
using PiSharp.Runtime.Tools;

namespace PiSharp.Cli.Startup;

internal sealed class CliProcessShutdown : IDisposable
{
    private readonly PosixSignalRegistration[] _signals;
    private readonly CancellationTokenSource _interrupt = new();

    public CliProcessShutdown(bool handleInterrupt = false)
    {
        _signals = OperatingSystem.IsWindows() ? [] : handleInterrupt ?
            [PosixSignalRegistration.Create(PosixSignal.SIGINT, context => Cancel(context)),
             PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => Exit(context, 143)),
             PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => Exit(context, 129))] :
            [PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => Exit(context, 143)),
             PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => Exit(context, 129))];
    }

    public CancellationToken InterruptToken => _interrupt.Token;

    private void Cancel(PosixSignalContext context)
    {
        context.Cancel = true;
        _interrupt.Cancel();
    }

    private static void Exit(PosixSignalContext context, int exitCode)
    {
        context.Cancel = true;
        ProcessLifetime.KillTrackedProcesses();
        Environment.Exit(exitCode);
    }

    public void Dispose()
    {
        foreach (var registration in _signals) registration.Dispose();
        _interrupt.Dispose();
    }
}
