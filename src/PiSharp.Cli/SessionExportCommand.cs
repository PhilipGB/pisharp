using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Standalone PiSharp-session HTML export without provider, resource or agent initialization.</summary>
public static class SessionExportCommand
{
    public static async Task<int> RunAsync(string[] arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(arguments[0]) ||
            arguments.Length == 2 && string.IsNullOrWhiteSpace(arguments[1]))
        {
            await error.WriteLineAsync("Use --export <PiSharp-session-file> [output.html].");
            return 2;
        }
        try
        {
            var input = Path.GetFullPath(arguments[0]);
            var info = new FileInfo(input);
            if (info.LinkTarget is not null) throw new InvalidDataException("Refusing a symbolic-link session file.");
            if (info.Length > 64 * 1024 * 1024) throw new InvalidDataException("Session file exceeds the 64MB export limit.");
            var session = ConversationSession.Parse(await File.ReadAllTextAsync(input));
            var destination = arguments.Length == 2 ? Path.GetFullPath(arguments[1]) :
                input.EndsWith(".session.json", StringComparison.OrdinalIgnoreCase)
                    ? input[..^".session.json".Length] + ".html" : input + ".html";
            await SessionExport.ExportHtmlAsync(session, destination);
            await output.WriteLineAsync($"Exported private PiSharp HTML to {destination}. Review before sharing.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        {
            // A corrupted session may contain credentials. Do not include exception payloads in stderr.
            await error.WriteLineAsync("Could not export the session. Check that the input is a valid PiSharp session, is not a symlink, and the destination does not already exist.");
            return 2;
        }
    }
}
