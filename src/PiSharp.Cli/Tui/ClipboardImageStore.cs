namespace PiSharp.Cli.Tui;

/// <summary>Persists a validated clipboard image so the agent's read tool can open the pasted path.</summary>
internal static class ClipboardImageStore
{
    public static async Task<string> SaveAsync(TerminalClipboardImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Bytes.Length == 0 || image.Bytes.Length > 20 * 1024 * 1024)
            throw new InvalidDataException("Clipboard image exceeds the 20 MiB limit.");
        var extension = image.MimeType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => throw new InvalidDataException($"Unsupported clipboard image type: {image.MimeType}")
        };
        var path = Path.Combine(Path.GetTempPath(), $"pisharp-clipboard-{Guid.NewGuid():N}{extension}");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using var stream = new FileStream(path, options);
            await stream.WriteAsync(image.Bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return path;
        }
        catch
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
