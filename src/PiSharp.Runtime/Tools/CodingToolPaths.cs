using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Tools;

internal static class CodingToolPaths
{
    public static string Resolve(string path, string workingDirectory, bool allowNullBytes = false)
    {
        path = Regex.Replace(path, "[\u00a0\u2000-\u200a\u202f\u205f\u3000]", " ");
        if (path.StartsWith('@')) path = path[1..];
        if (OperatingSystem.IsWindows() && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\'))
        {
            var drive = Regex.Match(path, "^/(?:mnt/|cygdrive/)?([a-z])(?:/(.*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (drive.Success) path = drive.Groups[1].Value.ToUpperInvariant() + ":\\" + drive.Groups[2].Value.Replace('/', '\\');
        }
        if (path == "~") path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (path.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        if (path.StartsWith("file://", StringComparison.Ordinal))
        {
            var uri = new Uri(path, UriKind.Absolute);
            if (!OperatingSystem.IsWindows() && uri.Host is not ("" or "localhost"))
                throw new ToolFailureException("File URL host must be \"localhost\" or empty on " +
                    (OperatingSystem.IsLinux() ? "linux" : "darwin"));
            if (uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
                OperatingSystem.IsWindows() && uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
                throw new ToolFailureException("File URL path must not include encoded / characters");
            path = uri.LocalPath;
        }
        if (path.Contains('\0'))
        {
            var absolute = OperatingSystem.IsWindows() ? path : NormalizeInvalidPosixPath(path, workingDirectory);
            if (allowNullBytes) return absolute;
            throw new ToolFailureException("The argument 'path' must be a string, Uint8Array, or URL without null bytes. Received " + Inspect(absolute));
        }
        return Path.GetFullPath(path.Length == 0 ? "." : path, workingDirectory);
    }

    public static string ResolveRead(string path, string workingDirectory)
    {
        var resolved = Resolve(path, workingDirectory);
        if (Exists(resolved)) return resolved;
        var timeVariant = Regex.Replace(resolved, " (AM|PM)\\.", "\u202f$1.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (timeVariant != resolved && Exists(timeVariant)) return timeVariant;
        var decomposed = resolved.Normalize(NormalizationForm.FormD);
        if (decomposed != resolved && Exists(decomposed)) return decomposed;
        var quoteVariant = resolved.Replace('\'', '\u2019');
        if (quoteVariant != resolved && Exists(quoteVariant)) return quoteVariant;
        var combined = decomposed.Replace('\'', '\u2019');
        return combined != resolved && Exists(combined) ? combined : resolved;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string NormalizeInvalidPosixPath(string path, string workingDirectory)
    {
        var parts = new List<string>();
        foreach (var part in (path.StartsWith('/') ? path : workingDirectory + "/" + path).Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    internal static string Inspect(string value)
    {
        var quote = !value.Contains('\'') ? '\'' : !value.Contains('"') ? '"' :
            !value.Contains('`') && !value.Contains("${", StringComparison.Ordinal) ? '`' : '\'';
        var output = new StringBuilder().Append(quote);
        foreach (var character in value)
        {
            if (character == quote || character == '\\') output.Append('\\').Append(character);
            else output.Append(character switch
            {
                '\0' => "\\x00",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\v' => "\\v",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' => "\\x" + ((int)character).ToString("X2", System.Globalization.CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        }
        return output.Append(quote).ToString();
    }
}
