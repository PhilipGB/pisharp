using System.Text;
using PiSharp.Core;

namespace PiSharp.Runtime;

/// <summary>Private, atomic v3 JSONL files; does not yet replace MAF session snapshots.</summary>
public static class PiSessionFiles
{
    public static async Task<PiSessionJournal> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        return PiSessionJournal.Parse(text);
    }

    public static async Task SaveAsync(PiSessionJournal journal, string path, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".pisharp-" + Guid.NewGuid().ToString("N") + ".tmp");
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
            await using (var stream = new FileStream(temporary, options))
            {
                var bytes = Encoding.UTF8.GetBytes(journal.ToJsonLines());
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
