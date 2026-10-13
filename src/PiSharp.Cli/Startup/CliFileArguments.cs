using System.Security;
using System.Text;
using Microsoft.Extensions.AI;

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
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePath(file, workingDirectory);
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException($"File not found: {path}", path);
            if (info.Length == 0) continue;
            var mediaType = GetImageMediaType(path);
            if (mediaType is not null)
            {
                const int maxImageBytes = 20 * 1024 * 1024;
                if (info.Length > maxImageBytes) throw new InvalidDataException($"Image attachment exceeds 20MB: {path}");
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                if (!HasImageSignature(bytes, mediaType)) throw new InvalidDataException($"Image attachment is invalid or does not match its extension: {path}");
                images.Add(new DataContent(bytes, mediaType));
                content.Append("<image name=\"").Append(SecurityElement.Escape(path)).Append("\" />\n");
                continue;
            }
            var bytesText = await File.ReadAllBytesAsync(path, cancellationToken);
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
            if (content.Length > 0) content.Append('\n');
            content.Append(prompt);
        }
        return new(content.ToString(), images);
    }

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

    private static bool HasImageSignature(ReadOnlySpan<byte> data, string mediaType) => mediaType switch
    {
        "image/png" => data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
        "image/jpeg" => data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff,
        "image/gif" => data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8),
        "image/webp" => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8),
        _ => false
    };

    private static string? GetImageMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null
    };
}
