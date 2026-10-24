using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Runtime.Tools;

internal static class LinuxFileAccess
{
    internal readonly record struct Ownership(uint UserId, uint GroupId);

    public static Ownership ReadOwnership(string path)
    {
        const uint ownershipMask = 0x18;
        if (statx(-100, path, 0, ownershipMask, out var status) != 0)
            throw Failure(Marshal.GetLastPInvokeError(), "stat", path);
        if ((status.Mask & ownershipMask) != ownershipMask) throw Failure(95, "stat", path);
        return new(status.UserId, status.GroupId);
    }

    public static void RestoreOwnership(SafeFileHandle handle, Ownership original, string path)
    {
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (fchown(descriptor, original.UserId, original.GroupId) != 0)
            throw Failure(Marshal.GetLastPInvokeError(), "chown", path);
    }

    public static UnixFileMode ModeAfterWrite(UnixFileMode mode)
    {
        var cleared = UnixFileMode.SetUser;
        if ((mode & UnixFileMode.GroupExecute) != 0) cleared |= UnixFileMode.SetGroup;
        if ((mode & cleared) == 0) return mode;
        try
        {
            foreach (var line in File.ReadLines("/proc/thread-self/status"))
                if (line.StartsWith("CapEff:", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(7).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var capabilities))
                    return (capabilities & (1UL << 4)) != 0 ? mode : mode & ~cleared;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return mode & ~cleared;
    }

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

    public static int AccessError(string path, int mode) =>
        OperatingSystem.IsLinux() && access(path, mode) != 0 ? Marshal.GetLastPInvokeError() : 0;

    public static string ErrorCode(int error) => Describe(error).Code;

    public static ToolFailureException Failure(int error, string operation, string? path = null)
    {
        var (code, description) = Describe(error);
        return new($"{code}: {description}, {operation}" + (path is null ? "" : $" '{path}'"));
    }

    private static (string Code, string Description) Describe(int error) => error switch
    {
        1 => ("EPERM", "operation not permitted"),
        2 => ("ENOENT", "no such file or directory"),
        5 => ("EIO", "i/o error"),
        12 => ("ENOMEM", "not enough memory"),
        13 => ("EACCES", "permission denied"),
        17 => ("EEXIST", "file already exists"),
        20 => ("ENOTDIR", "not a directory"),
        21 => ("EISDIR", "illegal operation on a directory"),
        22 => ("EINVAL", "invalid argument"),
        24 => ("EMFILE", "too many open files"),
        28 => ("ENOSPC", "no space left on device"),
        30 => ("EROFS", "read-only file system"),
        36 => ("ENAMETOOLONG", "name too long"),
        38 => ("ENOSYS", "function not implemented"),
        40 => ("ELOOP", "too many symbolic links encountered"),
        95 => ("ENOTSUP", "operation not supported"),
        _ => ("UNKNOWN", "unknown error " + error)
    };

    [DllImport("libc", SetLastError = true)]
    private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc")]
    private static extern void free(IntPtr buffer);

    // Linux statx has a fixed 256-byte UAPI layout on every architecture.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(24)] public uint GroupId;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxStatus status);

    [DllImport("libc", SetLastError = true)]
    private static extern int fchown(int descriptor, uint owner, uint group);
}
