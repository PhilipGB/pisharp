using PiSharp.Cli.Sessions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class TerminalSessionResumeCommandTests
{
    [Fact]
    public void DefaultSessionDirectoryUsesThePersistedSessionId()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-resume-cwd-" + Guid.NewGuid().ToString("N"));
        var store = new ConversationStore(workingDirectory);
        var conversation = new ConversationSession(workingDirectory, "fixture-model", null, "fixture");
        var path = Persist(Path.GetTempPath(), conversation);

        var command = TerminalSessionResumeCommand.Format(conversation, path, store, outputIsTerminal: true);

        Assert.Equal($"pisharp --session {conversation.Id}", command);
        File.Delete(path);
    }

    [Fact]
    public void CustomSessionDirectoryIsQuotedForTheResumeCommand()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-resume-cwd-" + Guid.NewGuid().ToString("N"));
        var sessionDirectory = Path.Combine(Path.GetTempPath(), "pisharp resume sessions " + Guid.NewGuid().ToString("N"));
        var store = new ConversationStore(workingDirectory, sessionDirectory);
        var conversation = new ConversationSession(workingDirectory, "fixture-model", null, "fixture");
        var path = Persist(sessionDirectory, conversation);

        var command = TerminalSessionResumeCommand.Format(conversation, path, store, outputIsTerminal: true);

        Assert.Equal($"pisharp --session-dir '{sessionDirectory}' --session {conversation.Id}", command);
        File.Delete(path);
        Directory.Delete(sessionDirectory);
    }

    [Fact]
    public void ResumeCommandIsOmittedForRedirectedOrUnpersistedSessions()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "pisharp-resume-cwd-" + Guid.NewGuid().ToString("N"));
        var store = new ConversationStore(workingDirectory);
        var conversation = new ConversationSession(workingDirectory, "fixture-model", null, "fixture");
        var path = Persist(Path.GetTempPath(), conversation);

        Assert.Null(TerminalSessionResumeCommand.Format(conversation, path, store, outputIsTerminal: false));
        Assert.Null(TerminalSessionResumeCommand.Format(conversation, null, store, outputIsTerminal: true));
        Assert.Null(TerminalSessionResumeCommand.Format(conversation, path + ".missing", store, outputIsTerminal: true));
        File.Delete(path);
    }

    private static string Persist(string directory, ConversationSession conversation)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, conversation.Id + ".session.json");
        File.WriteAllText(path, conversation.ToJson());
        return path;
    }
}
