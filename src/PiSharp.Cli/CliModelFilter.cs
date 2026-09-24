namespace PiSharp.Cli;

/// <summary>Match model list search tokens against provider and ID, following pinned Pi's fuzzy.ts matching rules.</summary>
public static class CliModelFilter
{
    public static bool Matches(string? pattern, string? provider, string model)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        var text = (provider + " " + model).ToLowerInvariant();
        return pattern.Split([' ', '\t', '\r', '\n', '/'], StringSplitOptions.RemoveEmptyEntries)
            .All(token => MatchesToken(token.ToLowerInvariant(), text));
    }

    private static bool MatchesToken(string token, string text)
    {
        if (InOrder(token, text)) return true;
        var boundary = 0;
        while (boundary < token.Length && char.IsAsciiLetter(token[boundary])) boundary++;
        if (boundary > 0 && boundary < token.Length && token[boundary..].All(char.IsAsciiDigit) &&
            InOrder(token[boundary..] + token[..boundary], text)) return true;
        boundary = 0;
        while (boundary < token.Length && char.IsAsciiDigit(token[boundary])) boundary++;
        return boundary > 0 && boundary < token.Length && token[boundary..].All(char.IsAsciiLetter) &&
            InOrder(token[boundary..] + token[..boundary], text);
    }

    private static bool InOrder(string token, string text)
    {
        if (token.Length > text.Length) return false;
        var position = 0;
        foreach (var character in token)
        {
            position = text.IndexOf(character, position);
            if (position < 0) return false;
            position++;
        }
        return true;
    }
}
