using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed class SessionBranchController(
    ConversationStore store,
    bool noSession,
    Func<ConversationSession, string?, Task<ConversationRun>> openRun)
{
    public async Task<SessionBranchResult> ForkAtUserAsync(ConversationSession source, string? sourcePath,
        string idPrefix)
    {
        var matches = source.ForkableUserMessages()
            .Where(item => item.Id.StartsWith(idPrefix, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException("Specify a unique user message id prefix from /fork.");
        var selected = matches[0];
        var (forked, prompt) = source.ForkAtUser(selected.Id);
        return await CreateBranchAsync(source, sourcePath, forked, selected.Id, prompt);
    }

    public async Task<SessionBranchResult> CloneAsync(ConversationSession source, string? sourcePath)
    {
        var clone = source.Fork();
        return await CreateBranchAsync(source, sourcePath, clone, null, null);
    }

    private async Task<SessionBranchResult> CreateBranchAsync(ConversationSession source, string? sourcePath,
        ConversationSession branch, string? sourceEntryId, string? prompt)
    {
        if (sourcePath is not null) await store.SaveAsync(source, sourcePath);
        var branchPath = noSession ? null : store.NewPath(branch);
        var run = await openRun(branch, branchPath);
        if (branchPath is not null) await store.SaveAsync(branch, branchPath);
        return new(branch, run, branchPath, sourceEntryId, prompt);
    }
}

internal sealed record SessionBranchResult(ConversationSession Conversation, ConversationRun Run, string? Path,
    string? SourceEntryId, string? Prompt);
