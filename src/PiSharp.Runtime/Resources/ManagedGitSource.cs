using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Resources;

/// <summary>A Pi-compatible Git package reference and its version-independent identity.</summary>
internal sealed record ManagedGitSource(string Repository, string Host, string Path, string? Ref)
{
    internal string Identity => $"git:{Host}/{Path}";

    internal static bool TryParse(string source, out ManagedGitSource parsed)
    {
        parsed = default!;
        var trimmed = source.Trim();
        var hasPrefix = trimmed.StartsWith("git:", StringComparison.Ordinal);
        var value = hasPrefix ? trimmed[4..].Trim() : trimmed;
        if (!hasPrefix && !Regex.IsMatch(value, "^(https?|ssh|git)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        string repository;
        string host;
        string path;
        string? reference;

        var scp = Regex.Match(value, "^git@([^:]+):(.+)$", RegexOptions.CultureInvariant);
        if (scp.Success)
        {
            if (!hasPrefix) return false;
            host = scp.Groups[1].Value;
            var rawPath = scp.Groups[2].Value;
            if (rawPath.StartsWith('/')) return false;
            (path, reference) = SplitReference(rawPath);
            repository = $"git@{host}:{path}";
        }
        else if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https" or "ssh" or "git")) return false;
            host = uri.IdnHost.ToLowerInvariant();
            var rawPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimStart('/');
            (path, reference) = SplitReference(rawPath);
            if (reference is null && !string.IsNullOrEmpty(uri.Fragment))
                reference = Uri.UnescapeDataString(uri.Fragment[1..]);
            var builder = new UriBuilder(uri) { Path = "/" + path, Fragment = "" };
            repository = builder.Uri.AbsoluteUri.TrimEnd('/');
        }
        else
        {
            if (!hasPrefix) return false;
            var slash = value.IndexOf('/');
            if (slash < 1) return false;
            host = value[..slash].ToLowerInvariant();
            if (!host.Contains('.') && host != "localhost") return false;
            (path, reference) = SplitReference(value[(slash + 1)..]);
            repository = $"https://{host}/{path}";
        }

        path = path.TrimStart('/').TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (!IsSafePart(host, allowSlash: false) || !IsSafePart(path, allowSlash: true) ||
            path.Split('/').Length < 2 || (reference is not null && !IsSafeReference(reference))) return false;

        parsed = new(repository, host, path, reference);
        return true;
    }

    private static (string Path, string? Ref) SplitReference(string path)
    {
        var index = path.IndexOf('@');
        if (index <= 0 || index == path.Length - 1) return (path, null);
        return (path[..index], Uri.UnescapeDataString(path[(index + 1)..]));
    }

    private static bool IsSafePart(string value, bool allowSlash)
    {
        string decoded;
        try { decoded = Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return false; }
        foreach (var candidate in new[] { value, decoded })
        {
            if (candidate.Length == 0 || candidate.Contains('\0') || candidate.Contains('\\') || candidate.StartsWith('/'))
                return false;
            if (!allowSlash && (candidate.Contains('/') || candidate.Contains(':'))) return false;
            if (candidate.Split('/').Any(segment => segment is ".." or ".")) return false;
        }
        return true;
    }

    private static bool IsSafeReference(string reference) => reference.Length > 0 && reference[0] != '-' &&
        !reference.Any(char.IsControl) && !reference.Contains('\\') && !reference.Contains('\0');
}
