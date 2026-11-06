using PiSharp.Cli.Composition;
using PiSharp.Cli.Tui;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed class TerminalSessionNavigation(ActiveProjectRuntime projectBinding,
    ActiveConversationRuntime conversationBinding, ProjectConversationTransitions projectTransitions, TerminalPresentation presentation,
    Func<ProjectSessionRuntime, Task> adoptProjectSession)
{
    private TerminalSessionPicker? _picker = presentation.Editor is null ? null : new TerminalSessionPicker(projectBinding.Store, presentation.Editor);
    private readonly TerminalForkPicker? _forkPicker = presentation.Editor is null ? null : new TerminalForkPicker(presentation.Editor);
    private Func<bool>? _isRunActive;
    private Func<CancellationToken, Task>? _settleRun;
    public void RefreshPicker() => _picker = presentation.Editor is null ? null : new TerminalSessionPicker(projectBinding.Store, presentation.Editor);

    /// <summary>Lets tree navigation stop and settle an in-flight response before it changes the head.</summary>
    public void ConfigureActiveRunInterruption(Func<bool> isRunActive, Func<CancellationToken, Task> settleRun)
    {
        ArgumentNullException.ThrowIfNull(isRunActive);
        ArgumentNullException.ThrowIfNull(settleRun);
        _isRunActive = isRunActive;
        _settleRun = settleRun;
    }

    public async Task ResumeSessionAsync(SessionListing listing)
    {
        if (listing.Path == conversationBinding.Path)
        {
            Console.WriteLine("Already in this session.");
            return;
        }

        var sourceStore = SourceStoreFor(listing);
        await sourceStore.ValidateListingUnchangedAsync(listing, CancellationToken.None);
        await projectTransitions.BeforeSessionSwitchAsync(PiSharpExtensionSessionSwitchReason.Resume,
            listing.Path, CancellationToken.None);
        await projectTransitions.SwitchAsync(listing.Path, adoptProjectSession, CancellationToken.None, listing);
        Console.WriteLine($"Resumed {conversationBinding.Conversation.Id[..12]} · {conversationBinding.Conversation.Name ?? "(unnamed)"}");
    }

    private ConversationStore SourceStoreFor(SessionListing listing)
    {
        if (listing.WorkingDirectory is null) return projectBinding.Store;
        var currentDirectory = projectBinding.Store.WorkingDirectory;
        if (ConversationStore.WorkingDirectoriesMatch(listing.WorkingDirectory, currentDirectory) &&
            SameDirectory(listing.Path, projectBinding.Store.DirectoryPath)) return projectBinding.Store;
        return new ConversationStore(listing.WorkingDirectory, Path.GetDirectoryName(listing.Path));
    }

    private static bool SameDirectory(string path, string? directory) => directory is not null &&
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async Task SelectSessionAsync()
    {
        if (projectBinding.Arguments.NoSession) throw new InvalidOperationException("Cannot resume in --no-session mode.");
        if (_picker is null)
        {
            Console.WriteLine("Use /sessions, then /resume <ID-prefix|exact-name> in interactive mode.");
            return;
        }
        var listing = await _picker.ShowAsync(conversationBinding.Path, conversationBinding.Conversation);
        if (listing is not null) await ResumeSessionAsync(listing);
    }

    public async Task ForkFromUserAsync(string id)
    {
        var matches = conversationBinding.Conversation.UserMessagesForForking()
            .Where(item => item.Id.StartsWith(id, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException("Specify a unique user message id prefix from /fork.");
        var selectedEntryId = matches[0].Id;
        await projectTransitions.BeforeSessionForkAsync(selectedEntryId, PiSharpExtensionForkPosition.Before,
            CancellationToken.None);
        var branch = await projectTransitions.Sessions.ForkAtUserAsync(conversationBinding.Conversation,
            conversationBinding.Path, selectedEntryId);
        await projectTransitions.AdoptSessionBranchAsync(branch, PiSharpExtensionSessionStartReason.Fork);
        presentation.LoadSessionTranscript();
        if (branch.Prompt is { } draft) presentation.Editor?.Prefill(draft);
        Console.WriteLine($"Forked {branch.SourceEntryId![..12]} to {branch.Path ?? "(ephemeral)"}. Edit and submit the draft prompt.");
    }

    public async Task SelectForkAsync()
    {
        var forkable = conversationBinding.Conversation.UserMessagesForForking();
        if (forkable.Count == 0)
        {
            Console.WriteLine("No user messages with text are available to fork.");
            return;
        }
        if (_forkPicker is null)
        {
            foreach (var item in forkable)
                Console.WriteLine($"{item.Id[..12]} · {new string(item.Text.Replace('\n', ' ').Take(90).Select(c => char.IsControl(c) ? ' ' : c).ToArray())}");
            Console.WriteLine("Use /fork <user-message-id> to edit a copy of its prompt in a new session.");
            return;
        }
        var selected = _forkPicker.Show(forkable);
        if (selected is { } message) await ForkFromUserAsync(message.Id);
    }

    public async Task SelectTreePointAsync()
    {
        var conversation = conversationBinding.Conversation;
        var entries = conversation.Tree.Entries;
        if (entries.Count == 0)
        {
            Console.WriteLine("No entries in session");
            return;
        }
        var editor = presentation.Editor;
        if (editor is null)
        {
            foreach (var entry in entries)
                Console.WriteLine($"{Short(entry.Id)} · {entry.Type} · {entry.Timestamp:HH:mm:ss}");
            return;
        }

        string? reopenId = null;
        while (true)
        {
            var selected = await editor.ShowTreeSelectorAsync(
                () => conversationBinding.Conversation.Tree.Entries,
                SetEntryLabelAsync,
                CopyTreeEntryAsync,
                conversationBinding.Conversation.Tree.HeadId,
                reopenId).ConfigureAwait(false);
            if (selected is null) return;
            if (selected.Id == conversationBinding.Conversation.Tree.HeadId)
            {
                Console.WriteLine("Already at this point");
                return;
            }

            var (resume, summarize, customInstructions) =
                await ResolveBranchSummaryChoiceAsync().ConfigureAwait(false);
            if (resume)
            {
                reopenId = selected.Id;
                continue;
            }

            // Pinned Pi stops an active response before committing a tree navigation.
            await InterruptActiveRunAsync(CancellationToken.None).ConfigureAwait(false);

            var branchSummarySettings = projectBinding.Settings.BranchSummary ?? new BranchSummarySettings();
            var options = new ConversationTreeSelectionOptions(Summarize: summarize,
                CustomInstructions: customInstructions,
                ReserveTokens: branchSummarySettings.EffectiveReserveTokens);
            ConversationTreeNavigationResult navigation;
            if (summarize && presentation.Screen is { IsActive: true })
            {
                // Keep reading input while the summary runs so Escape can abort it.
                var outcome = await editor.RunProgressAsync("Summarizing branch…", "Branch summary",
                    "Cancel branch summary?", "The branch will not be summarized.",
                    (token, _) => conversationBinding.Run.NavigateTreeAsync(selected.Id, options, token),
                    () => Task.CompletedTask, confirmCancellation: false).ConfigureAwait(false);
                if (outcome.Cancelled)
                {
                    Console.WriteLine("Branch summarization cancelled");
                    reopenId = selected.Id;
                    continue;
                }
                navigation = outcome.Value!;
            }
            else
            {
                navigation = await conversationBinding.Run.NavigateTreeAsync(selected.Id, options)
                    .ConfigureAwait(false);
            }

            presentation.LoadSessionTranscript();
            // Pinned Pi never replaces a non-empty draft with the navigated prompt.
            if (navigation.EditorText is { } draft && string.IsNullOrWhiteSpace(editor.Draft))
                editor.Prefill(draft);
            Console.WriteLine($"Selected {Short(selected.Id)} · {selected.Type}");
            return;
        }
    }

    private async Task InterruptActiveRunAsync(CancellationToken cancellationToken)
    {
        if (_isRunActive?.Invoke() != true) return;
        var pending = conversationBinding.Run.ClearPendingPrompts().InDeliveryOrder;
        presentation.Editor?.RestorePending(pending);
        if (_settleRun is not null) await _settleRun(cancellationToken).ConfigureAwait(false);
    }

    private static string Short(string id) => id[..Math.Min(12, id.Length)];

    /// <summary>Asks about branch summarization; escape resumes the tree navigator at the same selection.</summary>
    private async Task<(bool Resume, bool Summarize, string? CustomInstructions)> ResolveBranchSummaryChoiceAsync()
    {
        var branchSummarySettings = projectBinding.Settings.BranchSummary ?? new BranchSummarySettings();
        if (branchSummarySettings.SkipPrompt == true) return (false, false, null);
        var editor = presentation.Editor!;
        while (true)
        {
            var choice = editor.ShowInlineSelectionList("Summarize branch?",
                [
                    new TerminalSelectionOption<string>("no", "no", "No summary"),
                    new TerminalSelectionOption<string>("summarize", "summarize", "Summarize"),
                    new TerminalSelectionOption<string>("custom", "custom", "Summarize with custom prompt")
                ], [""], "enter select • escape/ctrl+c close", selectedKey: "no")?.Option.Value;
            if (choice is null) return (true, false, null);
            if (choice == "custom")
            {
                var values = await editor.PromptSequenceAsync("Custom summarization instructions",
                    [("Custom summarization instructions", null)]).ConfigureAwait(false);
                if (values is null) continue;
                return (false, true, values.FirstOrDefault());
            }
            return (false, choice == "summarize", null);
        }
    }

    private async Task<bool> SetEntryLabelAsync(string entryId, string? label)
    {
        try
        {
            await conversationBinding.Run.SetEntryLabelAsync(entryId, label).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            presentation.Screen?.AppendStatusMessage(error.Message, TerminalStatusNotificationKind.Error);
            return false;
        }
    }

    private async Task CopyTreeEntryAsync(string? text)
    {
        var screen = presentation.Screen;
        if (string.IsNullOrWhiteSpace(text))
        {
            screen?.AppendStatusMessage("Selected entry has no text to copy", TerminalStatusNotificationKind.Error);
            return;
        }
        try
        {
            await presentation.Clipboard.CopyTextAsync(text).ConfigureAwait(false);
            screen?.AppendStatusMessage("Copied selected message to clipboard");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            screen?.AppendStatusMessage("Could not copy to clipboard: " + error.Message,
                TerminalStatusNotificationKind.Error);
        }
    }

    public async Task ResumeCommandAsync(string argument)
    {
        if (projectBinding.Arguments.NoSession) throw new InvalidOperationException("Cannot resume in --no-session mode.");
        if (argument.Length == 0) await SelectSessionAsync();
        else await ResumeSessionAsync(SessionCatalog.Resolve(await SessionCatalog.ListAsync(projectBinding.Store), argument));
    }

    public async Task NewOrCloneAsync(string command)
    {
        SessionBranchResult branch;
        var reason = command == "/clone" ? PiSharpExtensionSessionStartReason.Fork : PiSharpExtensionSessionStartReason.New;
        if (command == "/clone")
        {
            var entryId = conversationBinding.Conversation.Tree.HeadId;
            if (entryId is null)
            {
                Console.WriteLine("Nothing to clone yet.");
                return;
            }
            await projectTransitions.BeforeSessionForkAsync(entryId, PiSharpExtensionForkPosition.At,
                CancellationToken.None);
            branch = await projectTransitions.Sessions.CloneAsync(conversationBinding.Conversation, conversationBinding.Path);
        }
        else
        {
            await projectTransitions.BeforeSessionSwitchAsync(PiSharpExtensionSessionSwitchReason.New,
                targetSessionFile: null, CancellationToken.None);
            branch = await projectTransitions.Sessions.NewAsync(conversationBinding.Conversation,
                conversationBinding.Path, conversationBinding.Path, conversationBinding.Run.ThinkingLevel);
        }
        await projectTransitions.AdoptSessionBranchAsync(branch, reason);
        presentation.LoadSessionTranscript();
        Console.WriteLine($"{command[1..]}: {conversationBinding.Path ?? "(ephemeral)"}");
    }
}
