using System.Text.RegularExpressions;

namespace PiSharp.Cli.Tui;

internal sealed record TerminalSkillInvocation(string Name, string Location, string Content, string? UserMessage);

internal static class TerminalSkillInvocationParser
{
    private static readonly Regex s_skillBlock = new(
        "^<skill name=\"([^\"]+)\" location=\"([^\"]+)\">\\n([\\s\\S]*?)\\n</skill>(?:\\n\\n([\\s\\S]+))?$",
        RegexOptions.CultureInvariant);

    public static TerminalSkillInvocation? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = s_skillBlock.Match(text);
        if (!match.Success) return null;
        var userMessage = match.Groups[4].Success ? match.Groups[4].Value.Trim() : null;
        return new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value,
            string.IsNullOrEmpty(userMessage) ? null : userMessage);
    }
}
