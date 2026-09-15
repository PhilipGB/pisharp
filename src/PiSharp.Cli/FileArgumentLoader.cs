using System.Text;

namespace PiSharp.Cli;

internal static class FileArgumentLoader
{
    public static async Task<string> BuildPromptAsync(
        string? prompt,
        IReadOnlyList<string> filePaths,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var fileContent = await ReadFilesAsync(filePaths, workspaceRoot, cancellationToken);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return fileContent;
        }
        if (string.IsNullOrEmpty(fileContent))
        {
            return prompt;
        }
        return $"{fileContent}\n{prompt}";
    }

    private static async Task<string> ReadFilesAsync(
        IEnumerable<string> paths,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolutePath = Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(workspaceRoot, path));
            if (!File.Exists(absolutePath))
            {
                throw new FileNotFoundException($"File argument not found: {path}", absolutePath);
            }

            var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
            if (content.Length == 0)
            {
                continue;
            }

            content = content.TrimStart('\uFEFF');
            builder.Append("<file name=\"")
                .Append(EscapeAttribute(absolutePath))
                .AppendLine("\">");
            builder.AppendLine(content);
            builder.AppendLine("</file>");
        }
        return builder.ToString().TrimEnd();
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}
