namespace PiSharp.Runtime.Tools;

/// <summary>Same-directory replace: cancellation never leaves a partially written target.</summary>
internal static class AtomicFileWriter
{
    public static async Task ReplaceAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken,
        Func<Task>? beforeCommit = null)
    {
        // Preserve the target of an existing symlink rather than replacing the link itself.
        // ResolveLinkTarget throws for paths which have not yet been created.
        if (File.Exists(path)) path = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, ".pisharp-" + Guid.NewGuid().ToString("N") + ".tmp");
        var originalMode = OperatingSystem.IsLinux() && File.Exists(path) ? File.GetUnixFileMode(path) : (UnixFileMode?)null;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (OperatingSystem.IsLinux())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(data, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            if (beforeCommit is not null) await beforeCommit();
            cancellationToken.ThrowIfCancellationRequested();
            // Uncommitted bytes stay private; restore existing permissions only when publishing the completed file.
            if (OperatingSystem.IsLinux() && originalMode is { } mode) File.SetUnixFileMode(temp, mode);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
