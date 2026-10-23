using System.Text;

namespace PiSharp.Runtime.Tools;

internal static class ShellSpawnValidation
{
    public static void RequireExecutable(string shell, string workingDirectory)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!Directory.Exists(workingDirectory)) throw new ToolFailureException("spawn ENOTDIR");
        if (LinuxFileAccess.AccessError(workingDirectory, 1) is { } cwdError && cwdError != 0)
            throw Failure(shell, cwdError);
        if (Directory.Exists(shell)) throw Failure(shell, 13);
        if (LinuxFileAccess.AccessError(shell, 1) is { } shellError && shellError != 0)
            throw Failure(shell, shellError);

        // setsid reports exec failures as exit codes; preserve spawn errors before starting that wrapper.
        Span<byte> header = stackalloc byte[256];
        int length;
        try
        {
            using var file = File.OpenRead(shell);
            length = file.Read(header);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        if (length < 2 || header[0] != '#' || header[1] != '!') return;
        var line = Encoding.UTF8.GetString(header[2..length]).Split('\n')[0].Trim(' ', '\t');
        var separator = line.IndexOfAny([' ', '\t']);
        var interpreter = separator < 0 ? line : line[..separator];
        if (interpreter.Length == 0) return;
        if (LinuxFileAccess.AccessError(interpreter, 1) is { } interpreterError && interpreterError != 0)
            throw Failure(shell, interpreterError);
        if (Directory.Exists(interpreter)) throw Failure(shell, 13);
    }

    private static ToolFailureException Failure(string shell, int error) => new($"spawn {shell} {LinuxFileAccess.ErrorCode(error)}");
}
