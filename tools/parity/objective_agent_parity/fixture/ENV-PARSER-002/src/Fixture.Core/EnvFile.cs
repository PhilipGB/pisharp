namespace Fixture.Core;

public static class EnvFile
{
    public static Dictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            var content = line.TrimStart();
            if (content.Length == 0 || content[0] == '#')
            {
                continue;
            }

            var parts = line.Split('=');
            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = parts[1];
        }

        return values;
    }
}
