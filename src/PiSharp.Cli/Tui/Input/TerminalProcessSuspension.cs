using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PiSharp.Cli.Tui;

internal static class TerminalProcessSuspension
{
    [UnsupportedOSPlatform("windows")]
    public static async Task SuspendAsync()
    {
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ignoreInterrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
        using var resume = PosixSignalRegistration.Create(PosixSignal.SIGCONT, context =>
        {
            context.Cancel = true;
            continued.TrySetResult();
        });

        // Signal the foreground job, including its child tools, while the shell stays in its own process group.
        var stopSignal = OperatingSystem.IsLinux() ? 20 : 18;
        if (kill(0, stopSignal) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not suspend the terminal process group.");
        await continued.Task;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
