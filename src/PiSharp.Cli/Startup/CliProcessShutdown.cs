using System.Runtime.InteropServices;
using PiSharp.Runtime.Tools;

namespace PiSharp.Cli.Startup;

internal sealed class CliProcessShutdown : IDisposable
{
    private readonly PosixSignalRegistration[] _signals;

    public CliProcessShutdown()
    {
        _signals = OperatingSystem.IsWindows() ? [] :
            [PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => Exit(context, 143)),
             PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => Exit(context, 129))];
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
    }
}
