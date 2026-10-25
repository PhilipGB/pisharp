using System.Security;
using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Tools;

namespace PiSharp.Cli;

/// <summary>Expands positional @file inputs into safe text and image prompt content.</summary>
public static class CliFileArguments
{
    public sealed record PromptFiles(string Text, IReadOnlyList<DataContent> Images);

    public static async Task<PromptFiles> ProcessFilesAsync(string prompt, IReadOnlyList<string>? files,
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (files is null || files.Count == 0) return new(prompt, []);
        var content = new StringBuilder();
        var images = new List<DataContent>();
        const int maxImageBytes = 20 * 1024 * 1024;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePath(file, workingDirectory);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException($"File not found: {path}", path);
            if (info.Length == 0) continue;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var mediaType = await ReadImageDetector.DetectAsync(stream, cancellationToken);
            if (mediaType is not null)
            {
                if (stream.Length > maxImageBytes)
                {
                    AppendImageOmission(content, path, "[Image omitted: exceeds the 20 MiB input limit.]");
                    continue;
                }
                byte[] bytes;
                try
                {
                    bytes = await ReadBoundedImageAsync(stream, maxImageBytes, path, cancellationToken);
                }
                catch (InvalidDataException) when (!cancellationToken.IsCancellationRequested)
                {
                    AppendImageOmission(content, path, "[Image omitted: exceeds the 20 MiB input limit.]");
                    continue;
                }
                if (mediaType == "image/bmp")
                {
                    var converted = ReadImageProcessor.Process(bytes, mediaType, cancellationToken, autoResizeImages: false);
                    if (converted.ImageMimeType is { } convertedMime && converted.ImageDataBase64 is { } encoded)
                        images.Add(new DataContent(Convert.FromBase64String(encoded), convertedMime));
                    var hintStart = converted.Text.IndexOf('\n');
                    var hints = hintStart < 0 ? "" : converted.Text[(hintStart + 1)..];
                    content.Append("<file name=\"").Append(SecurityElement.Escape(path)).Append("\">")
                        .Append(hints).Append("</file>\n");
                }
                else
                {
                    images.Add(new DataContent(bytes, mediaType));
                    content.Append("<file name=\"").Append(SecurityElement.Escape(path)).Append("\"></file>\n");
                }
                continue;
            }
            stream.Position = 0;
            using var textContent = new MemoryStream();
            await stream.CopyToAsync(textContent, cancellationToken);
            var bytesText = textContent.ToArray();
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytesText).TrimStart('\uFEFF');
                content.Append("<file name=\"").Append(SecurityElement.Escape(path)).Append("\">\n")
                    .Append(text).Append("\n</file>\n");
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException($"File is not UTF-8 text or a supported image: {path}", error);
            }
        }
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            content.Append(prompt);
        }
        return new(content.ToString(), images);
    }

    private static void AppendImageOmission(StringBuilder content, string path, string note) =>
        content.Append("<file name=\"").Append(SecurityElement.Escape(path)).Append("\">")
            .Append(note).Append("</file>\n");

    public static async Task<string> AppendTextFilesAsync(string prompt, IReadOnlyList<string>? files,
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (files is null || files.Count == 0) return prompt;
        var contents = new StringBuilder();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePath(file, workingDirectory);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException($"File not found: {path}", path);
            if (info.Length == 0) continue;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                contents.Append("<file name=\"").Append(SecurityElement.Escape(path)).Append("\">\n")
                    .Append(text).Append("\n</file>\n");
            }
            catch (DecoderFallbackException error)
            {
                throw new InvalidDataException($"File is not UTF-8 text (image and binary attachments are not supported yet): {path}", error);
            }
        }
        if (contents.Length == 0) return prompt;
        if (string.IsNullOrWhiteSpace(prompt)) return contents.ToString();
        return contents.Append('\n').Append(prompt).ToString();
    }

    private static string ResolvePath(string file, string workingDirectory)
    {
        var expanded = file == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) :
            file.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file[2..]) : file;
        return Path.GetFullPath(expanded, workingDirectory);
    }

    internal static async Task<byte[]> ReadBoundedImageAsync(Stream stream, int maxBytes, string path,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var remaining = maxBytes + 1L - output.Length;
            if (remaining <= 0) throw new InvalidDataException($"Image attachment exceeds 20MB: {path}");
            var count = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            if (output.Length > maxBytes) throw new InvalidDataException($"Image attachment exceeds 20MB: {path}");
        }
        return output.ToArray();
    }
}
