using System.Security;
using System.Text;

namespace PiSharp.Cli;

/// <summary>Expands positional @file inputs using Pi's textual file-attachment convention.</summary>
public static class CliFileArguments
{
    public static async Task<string> AppendTextFilesAsync(string prompt, IReadOnlyList<string>? files,
        string workingDirectory, CancellationToken cancellationToken = default)
    {
        if (files is null || files.Count == 0) return prompt;
        var contents = new StringBuilder();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expanded = file == "~" ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) :
                file.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file[2..]) : file;
            var path = Path.GetFullPath(expanded, workingDirectory);
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
            catch (DecoderFallbackException e)
            {
                throw new InvalidDataException($"File is not UTF-8 text (image and binary attachments are not supported yet): {path}", e);
            }
        }
        if (contents.Length == 0) return prompt;
        if (string.IsNullOrWhiteSpace(prompt)) return contents.ToString();
        return contents.Append('\n').Append(prompt).ToString();
    }
}
