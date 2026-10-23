using System.Text;
using PiSharp.Core;
using PiSharp.Runtime.Providers;

namespace PiSharp.Runtime.Tools;

/// <summary>Reads bounded text or images using the current model's image limits.</summary>
internal sealed class FileReader(string workingDirectory, ModelImageResizeOptions? imageResizeOptions)
{
    private ModelImageResizeOptions? _imageResizeOptions = imageResizeOptions;
    private int _supportsImages = 1;
    public ModelImageResizeOptions? ImageResizeOptions => Volatile.Read(ref _imageResizeOptions);
    public void SetImageResizeOptions(ModelImageResizeOptions? options) => Volatile.Write(ref _imageResizeOptions, options);
    public void SetModelSupportsImages(bool supportsImages) => Volatile.Write(ref _supportsImages, supportsImages ? 1 : 0);

    public async Task<ReadToolOutput> ReadAsync(string path, double offset, double? limit, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = CodingToolPaths.ResolveRead(path, workingDirectory);
            LinuxFileAccess.Require(resolved, 4, "access");
            if (OperatingSystem.IsLinux() && Directory.Exists(resolved)) throw LinuxFileAccess.Failure(21, "read");
            const int maxImageBytes = 20 * 1024 * 1024;
            await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            var mimeType = await ReadImageDetector.DetectAsync(stream, cancellationToken);
            if (mimeType is not null)
            {
                var supportsImages = Volatile.Read(ref _supportsImages) != 0;
                if (length > maxImageBytes)
                    return WithModelNote(new ReadToolOutput($"Read image file [{mimeType}]\n[Image omitted: could not be resized below the inline image size limit.]"), supportsImages);
                var bytes = new byte[checked((int)length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                var resizeOptions = Volatile.Read(ref _imageResizeOptions);
                var image = await Task.Run(() => ReadImageProcessor.Process(bytes, mimeType, cancellationToken, resizeOptions), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return WithModelNote(image, supportsImages);
            }
            if (length > 2 * 1024 * 1024)
            {
                var selection = await StreamingTextReader.SelectDetailedAsync(stream, path, offset, limit, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return new ReadToolOutput(selection.Text, Details: selection.Truncation is { } truncation ? new { truncation } : null);
            }
            var content = new byte[checked((int)length)];
            stream.Position = 0;
            await stream.ReadExactlyAsync(content, cancellationToken);
            var text = Encoding.UTF8.GetString(content);
            var selected = ReadTextPlanner.SelectDetailed(text, path, offset, limit);
            cancellationToken.ThrowIfCancellationRequested();
            return new ReadToolOutput(selected.Text, Details: selected.Truncation is { } details ? new { truncation = details } : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Operation aborted", cancellationToken);
        }
        catch (ArgumentOutOfRangeException e) { throw new ToolFailureException(e.Message, inner: e); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException($"Error reading {path}: {e.Message}", inner: e);
        }
    }

    private static ReadToolOutput WithModelNote(ReadToolOutput image, bool supportsImages) => supportsImages
        ? image : image with { Text = image.Text + "\n" + ReadToolOutput.NonVisionNote };
}
