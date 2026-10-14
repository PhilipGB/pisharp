using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PiSharp.Runtime.Resources;

/// <summary>Checks regular-file identity before a resource read can open a device or named pipe.</summary>
internal static class LocalResourceFileType
{
    private const int TypeMask = 0xf000;
    private const int RegularFile = 0x8000;

    internal static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            // statx has a fixed ABI across Linux architectures; mode is at byte 28.
            if (Statx(-100, path, 0, 1, out var status) != 0) throw StatError(path);
            return (status.Mode & TypeMask) == RegularFile;
        }
        if (OperatingSystem.IsMacOS())
        {
            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? DarwinStat64(path, buffer) : DarwinStat(path, buffer);
                if (result != 0) throw StatError(path);
                return (Marshal.ReadInt16(buffer, 4) & TypeMask) == RegularFile;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.Device) == 0;
    }

    private static IOException StatError(string path)
    {
        var code = Marshal.GetLastPInvokeError();
        return new IOException($"Could not inspect '{path}': {new Win32Exception(code).Message}");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(28)] internal ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxStatx status);

    [DllImport("libSystem.B.dylib", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int DarwinStat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr status);

    [DllImport("libSystem.B.dylib", EntryPoint = "stat", SetLastError = true)]
    private static extern int DarwinStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr status);
}
