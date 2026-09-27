using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Runtime.Sessions;

public sealed class PiJsonlSessionFileStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private string? _knownHash;

    public async Task SaveAsync(ConversationSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var folder = Path.GetDirectoryName(_path)!;
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(folder);
        var lockOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite
        };
        if (OperatingSystem.IsLinux()) lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var lease = new FileStream(_path + ".lock", lockOptions);
        if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Session file locking is not supported on macOS.");
        lease.Lock(0, 1);
        try
        {
            if (_knownHash is { } expected)
            {
                if (!File.Exists(_path) || Hash(await File.ReadAllBytesAsync(_path, cancellationToken)) != expected)
                    throw new InvalidDataException("Session changed on disk; reopen before writing.");
                if ((File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Refusing to replace a symbolic-link session.");
            }
            else if (File.Exists(_path))
            {
                throw new InvalidDataException("Session already exists; refusing to replace an unloaded file.");
            }

            var snapshot = Encoding.UTF8.GetBytes(PiJsonlSessionInterchange.Export(session));
            await AtomicSessionFileWriter.WriteAsync(snapshot, _path, folder, cancellationToken);
            _knownHash = Hash(snapshot);
        }
        finally { lease.Unlock(0, 1); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
