using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal static class TerminalSessionResumeCommand
{
    public static string? Format(ConversationSession conversation, string? sessionPath,
        ConversationStore store, bool outputIsTerminal)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(store);
        if (!outputIsTerminal || sessionPath is null || !File.Exists(sessionPath)) return null;

        var command = new List<string> { "pisharp" };
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(store.DirectoryPath).Equals(Path.GetFullPath(store.DefaultDirectoryPath), comparison))
        {
            command.Add("--session-dir");
            command.Add(QuoteIfNeeded(store.DirectoryPath));
        }
        command.Add("--session");
        command.Add(QuoteIfNeeded(conversation.Id));
        return string.Join(' ', command);
    }

    private static string QuoteIfNeeded(string value) => value.Length > 0 &&
        !value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.' or '/' or '~' or ':' or '@'))
        ? value
        : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
