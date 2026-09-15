using System.Text;
using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

internal sealed record FilePrompt(string Text, IReadOnlyList<AIContent> Images);

internal static class FileArgumentLoader
{
    private const long MaximumImageBytes = 20L * 1024 * 1024;

    public static async Task<string> BuildPromptAsync(
        string? prompt,
        IReadOnlyList<string> filePaths,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var result = await LoadAsync(prompt, filePaths, workspaceRoot, cancellationToken);
        return result.Text;
    }

    public static async Task<FilePrompt> LoadAsync(
        string? prompt,
        IReadOnlyList<string> filePaths,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var files = await ReadFilesAsync(filePaths, workspaceRoot, cancellationToken);
        var text = string.IsNullOrWhiteSpace(prompt)
            ? files.Text
            : string.IsNullOrEmpty(files.Text) ? prompt : $"{files.Text}\n{prompt}";
        return new FilePrompt(text, files.Images);
    }

    private static async Task<(string Text, IReadOnlyList<AIContent> Images)> ReadFilesAsync(
        IEnumerable<string> paths,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var images = new List<AIContent>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = ResolvePath(path, workspaceRoot);
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException($"File argument not found: {path}", absolutePath);
            }

            if (TryGetImageMediaType(absolutePath, out var mediaType))
            {
                var imageInfo = new FileInfo(absolutePath);
                if (imageInfo.Length > MaximumImageBytes)
                {
                    throw new InvalidDataException(
                        $"Image file exceeds the {MaximumImageBytes / (1024 * 1024)} MB limit: {path}");
                }
                var bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken);
                images.Add(new DataContent(bytes, mediaType));
                AppendFileMarker(builder, absolutePath);
                continue;
            }

            var content = (await File.ReadAllTextAsync(absolutePath, cancellationToken)).TrimStart('\uFEFF');
            if (content.Length == 0)
            {
                continue;
            }

            builder.Append("<file name=\"")
                .Append(EscapeAttribute(absolutePath))
                .AppendLine("\">");
            builder.AppendLine(content);
            builder.AppendLine("</file>");
        }
        return (builder.ToString().TrimEnd(), images);
    }

    private static string ResolvePath(string path, string workspaceRoot) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));

    private static void AppendFileMarker(StringBuilder builder, string absolutePath)
    {
        builder.Append("<file name=\"")
            .Append(EscapeAttribute(absolutePath))
            .AppendLine("\"></file>");
    }

    private static bool TryGetImageMediaType(string path, out string mediaType)
    {
        mediaType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => string.Empty,
        };
        return mediaType.Length > 0;
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
