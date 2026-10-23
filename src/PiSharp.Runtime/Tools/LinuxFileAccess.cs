using System.Runtime.InteropServices;

namespace PiSharp.Runtime.Tools;

internal static class LinuxFileAccess
{
    public static string? ResolveExistingPath(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var resolved = realpath(path, IntPtr.Zero);
        if (resolved != IntPtr.Zero)
        {
            try { return Marshal.PtrToStringUTF8(resolved)!; }
            finally { free(resolved); }
        }
        var error = Marshal.GetLastPInvokeError();
        if (error is 2 or 20) return null;
        throw Failure(error, "realpath", path);
    }

    public static void RequireWritableIfPresent(string path)
    {
        if (!OperatingSystem.IsLinux() || access(path, 2) == 0) return;
        var error = Marshal.GetLastPInvokeError();
        if (error is not (2 or 20)) throw Failure(error, "open", path);
    }

    public static void Require(string path, int mode, string operation)
    {
        if (OperatingSystem.IsLinux() && access(path, mode) != 0)
            throw Failure(Marshal.GetLastPInvokeError(), operation, path);
    }

    public static ToolFailureException Failure(int error, string operation, string? path = null)
    {
        var (code, description) = error switch
        {
            1 => ("EPERM", "operation not permitted"),
            2 => ("ENOENT", "no such file or directory"),
            5 => ("EIO", "i/o error"),
            12 => ("ENOMEM", "not enough memory"),
            13 => ("EACCES", "permission denied"),
            17 => ("EEXIST", "file already exists"),
            20 => ("ENOTDIR", "not a directory"),
            21 => ("EISDIR", "illegal operation on a directory"),
            24 => ("EMFILE", "too many open files"),
            28 => ("ENOSPC", "no space left on device"),
            30 => ("EROFS", "read-only file system"),
            36 => ("ENAMETOOLONG", "name too long"),
            40 => ("ELOOP", "too many symbolic links encountered"),
            _ => ("UNKNOWN", "unknown error " + error)
        };
        return new($"{code}: {description}, {operation}" + (path is null ? "" : $" '{path}'"));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc")]
    private static extern void free(IntPtr buffer);
}
