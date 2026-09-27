using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal static class SessionForkFactory
{
    private const long MaximumSourceBytes = 128L * 1024 * 1024;

    public static async Task<ConversationSession> CreateAsync(string sourcePath, string targetWorkingDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(sourcePath);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Fork source session was not found.", path);
        if (file.Length > MaximumSourceBytes)
            throw new InvalidDataException("Fork source session exceeds the 128 MiB limit.");

        var contents = await File.ReadAllTextAsync(path, cancellationToken);
        var source = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? PiJsonlSessionInterchange.Import(contents, targetWorkingDirectory)
            : ConversationSession.Parse(contents);
        return source.ForkInto(targetWorkingDirectory, path);
    }
}
