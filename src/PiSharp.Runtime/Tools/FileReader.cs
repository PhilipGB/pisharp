using System.Text;
using PiSharp.Core;
using PiSharp.Runtime.Providers;

namespace PiSharp.Runtime.Tools;

/// <summary>Reads bounded text or images using the current model's image limits.</summary>
internal sealed class FileReader(string workingDirectory, ModelImageResizeOptions? imageResizeOptions)
{
    private ModelImageResizeOptions? _imageResizeOptions = imageResizeOptions;
    public ModelImageResizeOptions? ImageResizeOptions => Volatile.Read(ref _imageResizeOptions);
    public void SetImageResizeOptions(ModelImageResizeOptions? options) => Volatile.Write(ref _imageResizeOptions, options);
    private string Resolve(string path) => Path.GetFullPath(path, workingDirectory);

    public async Task<ReadToolOutput> ReadAsync(string path, int offset, int? limit, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = Resolve(path);
            const int maxImageBytes = 20 * 1024 * 1024;
            await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            var mimeType = await ReadImageDetector.DetectAsync(stream, cancellationToken);
            if (mimeType is not null)
            {
                if (length > maxImageBytes)
                    return new ReadToolOutput($"Read image file [{mimeType}]\n[Image omitted: could not be resized below the inline image size limit.]");
                var bytes = new byte[checked((int)length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                var resizeOptions = Volatile.Read(ref _imageResizeOptions);
                return await Task.Run(() => ReadImageProcessor.Process(bytes, mimeType, cancellationToken, resizeOptions), cancellationToken);
            }
            if (offset < 1 || limit is <= 0) throw new ToolFailureException("offset and limit must be positive.");
            if (length > 2 * 1024 * 1024)
            {
                var selection = await StreamingTextReader.SelectDetailedAsync(resolved, path, offset, limit, cancellationToken);
                return new ReadToolOutput(selection.Text, Details: selection.Truncation is { } truncation ? new { truncation } : null);
            }
            var content = new byte[checked((int)length)];
            stream.Position = 0;
            await stream.ReadExactlyAsync(content, cancellationToken);
            var text = Encoding.UTF8.GetString(content);
            var selected = ReadTextPlanner.SelectDetailed(text, path, offset, limit);
            return new ReadToolOutput(selected.Text, Details: selected.Truncation is { } details ? new { truncation = details } : null);
        }
        catch (ArgumentOutOfRangeException e) { throw new ToolFailureException(e.Message.Split('\n')[0], inner: e); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException($"Error reading {path}: {e.Message}", inner: e);
        }
    }

}
