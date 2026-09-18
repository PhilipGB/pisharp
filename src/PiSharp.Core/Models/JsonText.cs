using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Core.Models;

/// <summary>
/// JSON text normalization utilities matching pinned Pi behaviour (packages/coding-agent/src/
/// utils/json.ts and text.ts): strip a UTF-8 BOM, then strip // line comments and trailing
/// commas while leaving string literals untouched.
/// </summary>
public static class JsonText
{
    // Verbatim strings so backslash counts match the regex source exactly.
    // Pattern 1: " (?: \\. | [^"\\] )* " | // [^\n] *
    private static readonly Regex StringOrLineComment =
        new(@"""(?:\\.|[^""\\])*""|//[^\n]*", RegexOptions.Compiled);

    // Pattern 2: " (?: \\. | [^"\\] )* " | , ( \s* [}\]] )
    private static readonly Regex StringOrTrailingComma =
        new(@"""(?:\\.|[^""\\])*""|,(\s*[}\]])", RegexOptions.Compiled);

    /// <summary>Removes a leading UTF-8 BOM if present (pinned Pi: stripBom).</summary>
    public static string StripBom(string content)
    {
        if (content.Length > 0 && content[0] == '\uFEFF') // BOM
        {
            return content[1..];
        }

        return content;
    }

    /// <summary>Strips // line comments and trailing commas, leaving string literals untouched.</summary>
    public static string StripJsonComments(string input)
    {
        var withoutComments = StringOrLineComment.Replace(
            input,
            match => match.Value.StartsWith('"') ? match.Value : string.Empty);
        return StringOrTrailingComma.Replace(
            withoutComments,
            match =>
            {
                var tail = match.Groups[1].Success ? match.Groups[1].Value : null;
                return tail ?? (match.Value.StartsWith('"') ? match.Value : string.Empty);
            });
    }

    /// <summary>Reads a UTF-8 file tolerating a BOM (pinned Pi reads with BOM stripping).</summary>
    public static string ReadFileText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return DecodeUtf8(bytes);
    }

    /// <summary>Async read of a UTF-8 file tolerating a BOM.</summary>
    public static async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return DecodeUtf8(memory.ToArray());
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
