namespace PiSharp.Cli.Sessions;

internal static class BoundedSessionFileReader
{
    internal const long MaximumSessionBytes = 128L * 1024 * 1024;

    public static byte[] ReadAllBytes(string path, string tooLargeMessage, string changedMessage)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        return ReadAllBytes(stream, MaximumSessionBytes, tooLargeMessage, changedMessage);
    }

    public static async Task<byte[]> ReadAllBytesAsync(string path, string tooLargeMessage, string changedMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadAllBytesAsync(stream, MaximumSessionBytes, tooLargeMessage, changedMessage,
            cancellationToken);
    }

    public static string ReadAllText(string path, string tooLargeMessage, string changedMessage)
    {
        var bytes = ReadAllBytes(path, tooLargeMessage, changedMessage);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static byte[] ReadAllBytes(Stream stream, long maximumBytes, string tooLargeMessage,
        string changedMessage)
    {
        var length = stream.Length;
        if (length > maximumBytes)
            throw new InvalidDataException(tooLargeMessage);

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException(changedMessage);
        return bytes;
    }

    internal static async Task<byte[]> ReadAllBytesAsync(Stream stream, long maximumBytes, string tooLargeMessage,
        string changedMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var length = stream.Length;
        if (length > maximumBytes)
            throw new InvalidDataException(tooLargeMessage);

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0)
            throw new InvalidDataException(changedMessage);
        return bytes;
    }
}
