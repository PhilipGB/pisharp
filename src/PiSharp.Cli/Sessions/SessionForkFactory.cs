using PiSharp.Runtime.Sessions;
using System.Security.Cryptography;

namespace PiSharp.Cli.Sessions;

internal static class SessionForkFactory
{
    private const long MaximumSourceBytes = 128L * 1024 * 1024;

    public static async Task<ConversationSession> CreateAsync(string sourcePath, string targetWorkingDirectory,
        CancellationToken cancellationToken = default, string? expectedFingerprint = null)
    {
        var path = Path.GetFullPath(sourcePath);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Fork source session was not found.", path);
        if (file.Length > MaximumSourceBytes)
            throw new InvalidDataException("Fork source session exceeds the 128 MiB limit.");
        if (expectedFingerprint is not null && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Session path is a symbolic link.");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength > MaximumSourceBytes)
            throw new InvalidDataException("Fork source session exceeds the 128 MiB limit.");
        if (expectedFingerprint is not null &&
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Session changed on disk; list sessions again before opening.");
        var contents = System.Text.Encoding.UTF8.GetString(bytes);
        var source = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? PiJsonlSessionInterchange.Import(contents, targetWorkingDirectory)
            : ConversationSession.Parse(contents);
        return source.ForkInto(targetWorkingDirectory, path);
    }
}
