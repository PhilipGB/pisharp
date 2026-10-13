using System.Text;
using System.Text.Json;

namespace PiSharp.Cli.Tui;

/// <summary>Shows a short, human-safe summary for built-in calls without exposing arbitrary extension arguments.</summary>
internal static class ToolCallSummary
{
    public static string? Format(string? tool, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null || tool is null) return null;
        string? path = Get(arguments, "path");
        return tool.ToLowerInvariant() switch
        {
            "bash" => Prefix("command", Get(arguments, "command")),
            "read" => Prefix("path", path),
            "write" or "edit" => Prefix("path", path),
            "grep" => Join(Prefix("pattern", Get(arguments, "pattern")), Prefix("path", path)),
            "find" => Join(Prefix("pattern", Get(arguments, "pattern")), Prefix("path", path)),
            "ls" => Prefix("path", path),
            _ => null
        };
    }

    private static string? Get(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        foreach (var pair in arguments)
        {
            if (!pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = pair.Value switch
            {
                string text => text,
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                _ => null
            };
            return value is null ? null : Preview(value);
        }
        return null;
    }

    private static string? Prefix(string name, string? value) => value is null ? null : $"{name}: {value}";

    private static string? Join(string? first, string? second) => (first, second) switch
    {
        (null, null) => null,
        (not null, null) => first,
        (null, not null) => second,
        _ => $"{first}; {second}"
    };

    private static string Preview(string value)
    {
        var output = new StringBuilder();
        var pendingSpace = false;
        var visibleRunes = 0;
        foreach (var rune in TerminalSafeText.Normalize(value).EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = output.Length > 0;
                continue;
            }
            if (visibleRunes == 96)
            {
                output.Append('…');
                break;
            }
            if (pendingSpace) output.Append(' ');
            output.Append(rune.ToString());
            pendingSpace = false;
            visibleRunes++;
        }
        return output.ToString();
    }
}
