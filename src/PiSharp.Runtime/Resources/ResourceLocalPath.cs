using System.Globalization;
using System.Text;

namespace PiSharp.Runtime.Resources;

public static class ResourceLocalPath
{
    // ECMAScript trim removes FEFF and preserves NEL, unlike the .NET whitespace set.
    private static readonly char[] s_whitespace = ['\t', '\n', '\v', '\f', '\r', ' ', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007',
        '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF'];
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    internal static bool IsLocal(string source)
    {
        var trimmed = source.Trim(s_whitespace);
        return !new[] { "npm:", "git:", "github:", "http:", "https:", "ssh:", "builtin:" }
            .Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static string Resolve(string source, string baseDirectory, bool trim = true)
    {
        var path = trim ? source.Trim(s_whitespace) : source;
        if (path.StartsWith("file://", StringComparison.Ordinal)) path = FileUrlPath(path);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") path = home;
        else if (path.StartsWith("~/", StringComparison.Ordinal) ||
                 OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(home, path[2..]);
        return Path.TrimEndingDirectorySeparator(path.Length == 0
            ? Path.GetFullPath(baseDirectory) : Path.GetFullPath(path, Path.GetFullPath(baseDirectory)));
    }

    internal static string FileUrlPath(string source)
    {
        Uri uri;
        try { uri = new Uri(source, UriKind.Absolute); }
        catch (UriFormatException) { throw new InvalidDataException("URI malformed"); }
        var localhost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows() && uri.Host.Length > 0 && !localhost)
            throw new InvalidDataException($"File URL host must be \"localhost\" or empty on {(OperatingSystem.IsMacOS() ? "darwin" : "linux")}");
        var end = source.IndexOfAny(['?', '#']);
        var start = source.IndexOf('/', "file://".Length);
        var escapedPath = start < 0 || end >= 0 && start > end ? "" : source[start..(end < 0 ? source.Length : end)];
        if (escapedPath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            OperatingSystem.IsWindows() && escapedPath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(OperatingSystem.IsWindows()
                ? "File URL path must not include encoded \\ or / characters"
                : "File URL path must not include encoded / characters");
        ValidateEscapes(escapedPath);
        if (localhost) uri = new UriBuilder(uri) { Host = "" }.Uri;
        return uri.LocalPath;
    }

    private static void ValidateEscapes(string path)
    {
        for (var index = 0; index < path.Length; index++)
        {
            if (path[index] != '%') continue;
            var bytes = new List<byte>();
            do
            {
                if (index + 2 >= path.Length || !byte.TryParse(path.AsSpan(index + 1, 2),
                        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidDataException("URI malformed");
                bytes.Add(value);
                index += 3;
            } while (index < path.Length && path[index] == '%');
            try { _ = s_utf8.GetString(bytes.ToArray()); }
            catch (DecoderFallbackException) { throw new InvalidDataException("URI malformed"); }
            index--;
        }
    }
}
