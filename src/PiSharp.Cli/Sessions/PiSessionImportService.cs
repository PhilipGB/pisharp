using PiSharp.Runtime.Sessions;
using System.Security.Cryptography;

namespace PiSharp.Cli;

internal sealed class PiSessionImportService(ConversationStore store, string projectDirectory, bool noSession)
{
    private const long MaximumImportBytes = 128L * 1024 * 1024;
    private readonly string _projectDirectory = Path.GetFullPath(projectDirectory);

    public ConversationSession ImportFile(string path, string? expectedFingerprint = null)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Pi session file was not found.", path);
        if (file.Length > MaximumImportBytes) throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");
        if (expectedFingerprint is not null && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Session path is a symbolic link.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > MaximumImportBytes) throw new InvalidDataException("Pi session exceeds the 128 MiB import limit.");
        if (expectedFingerprint is not null &&
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Session changed on disk; list sessions again before opening.");
        var imported = PiJsonlSessionInterchange.Import(System.Text.Encoding.UTF8.GetString(bytes),
            workingDirectoryFallback: _projectDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(imported.WorkingDirectory).Equals(_projectDirectory, comparison))
            throw new InvalidDataException($"Pi session working directory '{imported.WorkingDirectory}' differs from this project. Start PiSharp there before importing.");
        if (!Directory.Exists(imported.WorkingDirectory))
            throw new InvalidDataException($"Pi session working directory does not exist: {imported.WorkingDirectory}");
        return imported;
    }

    public string? CreateDestinationPath(ConversationSession imported)
    {
        if (noSession) return null;
        var path = store.NewPath(imported);
        const string extension = ".session.json";
        var stem = Path.GetFileName(path)[..^extension.Length];
        var directory = Path.GetDirectoryName(path)!;
        for (var suffix = 1; File.Exists(path); suffix++)
            path = Path.Combine(directory, $"{stem}-{suffix}{extension}");
        return path;
    }
}
