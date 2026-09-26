using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed class InteractiveSessionController(
    ConversationStore store,
    bool noSession,
    Func<ConversationSession, string?, Task<ConversationRun>> openRun)
{
    public async Task<SessionResumeResult?> ResumeAsync(ConversationSession current, string? currentPath,
        SessionListing listing, string currentModel, string? currentEndpoint, string currentProvider)
    {
        if (listing.Path == currentPath) return null;
        if (currentPath is not null) await store.SaveAsync(current, currentPath);
        var resumed = await store.LoadAsync(listing.Path);
        if (resumed.Model != currentModel || resumed.Endpoint != currentEndpoint ||
            resumed.Provider is not null && !resumed.Provider.Equals(currentProvider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Session uses another provider, model, or endpoint; open it directly with --session.");
        var run = await openRun(resumed, listing.Path);
        return new(resumed, run, listing.Path);
    }

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

    public async Task<SessionBranchResult> NewAsync(ConversationSession source, string? sourcePath,
        string? parentSessionPath = null)
    {
        if (sourcePath is not null) await store.SaveAsync(source, sourcePath);
        var conversation = new ConversationSession(source.WorkingDirectory, source.Model, source.Endpoint,
            source.Provider, parentSessionPath);
        var path = noSession ? null : store.NewPath(conversation);
        var run = await openRun(conversation, path);
        if (path is not null) await store.SaveAsync(conversation, path);
        return new(conversation, run, path, null, null);
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

internal sealed record SessionResumeResult(ConversationSession Conversation, ConversationRun Run, string Path);
