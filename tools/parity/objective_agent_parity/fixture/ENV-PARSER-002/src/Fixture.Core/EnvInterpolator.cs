using System.Text.RegularExpressions;

namespace Fixture.Core;

public static class EnvInterpolator
{
    private static readonly Regex Placeholder = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    public static string Expand(string input, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(values);

        return Placeholder.Replace(input, match =>
            values.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : string.Empty);
    }
}
