namespace PiSharp.Runtime.Sessions;

internal static class AtomicSessionFileWriter
{
    public static async Task WriteAsync(byte[] snapshot, string target, string folder,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(folder, ".pisharp-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temp, options))
            {
                await file.WriteAsync(snapshot, cancellationToken);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
