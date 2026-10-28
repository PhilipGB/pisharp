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
    private readonly TerminalTreePicker? _treePicker = presentation.Editor is null ? null : new TerminalTreePicker(presentation.Editor);
    private readonly TerminalForkPicker? _forkPicker = presentation.Editor is null ? null : new TerminalForkPicker(presentation.Editor);
    public void RefreshPicker() => _picker = presentation.Editor is null ? null : new TerminalSessionPicker(projectBinding.Store, presentation.Editor);

    public async Task ResumeSessionAsync(SessionListing listing)
    {
        if (listing.Path == conversationBinding.Path)
        {
            Console.WriteLine("Already in this session.");
            return;
        }

        await projectBinding.Store.ValidateListingUnchangedAsync(listing, CancellationToken.None);
        await projectTransitions.BeforeSessionSwitchAsync(PiSharpExtensionSessionSwitchReason.Resume,
            listing.Path, CancellationToken.None);
        await projectTransitions.SwitchAsync(listing.Path, adoptProjectSession, CancellationToken.None, listing);
        Console.WriteLine($"Resumed {conversationBinding.Conversation.Id[..12]} · {conversationBinding.Conversation.Name ?? "(unnamed)"}");
    }

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
            Console.WriteLine("No entries in session.");
            return;
        }
        if (_treePicker is null)
        {
            foreach (var entry in entries)
                Console.WriteLine($"{entry.Id[..12]} · {entry.Type} · {entry.Timestamp:HH:mm:ss}");
            return;
        }

        var selected = _treePicker.Show(entries, conversation.Tree.HeadId);
        if (selected is null) return;
        if (selected.Id == conversation.Tree.HeadId)
        {
            Console.WriteLine("Already at this point.");
            return;
        }

        var branchSummarySettings = projectBinding.Settings.BranchSummary ?? new BranchSummarySettings();
        var summarize = false;
        if (branchSummarySettings.SkipPrompt != true)
            summarize = presentation.Editor?.ShowInlineSelectionList("Carry context from the branch being left?",
                [
                    new TerminalSelectionOption<bool>("no", false, "Switch branches without a summary"),
                    new TerminalSelectionOption<bool>("yes", true, "Summarize the branch before switching")
                ], [""], "enter select • escape/ctrl+c continue without a summary", selectedKey: "no")?.Option.Value == true;
        var navigation = await conversationBinding.Run.NavigateTreeAsync(selected.Id,
            new ConversationTreeSelectionOptions(Summarize: summarize,
                ReserveTokens: branchSummarySettings.EffectiveReserveTokens));

        presentation.LoadSessionTranscript();
        if (navigation.EditorText is { } draft) presentation.Editor?.Prefill(draft);
        Console.WriteLine($"Selected {selected.Id[..12]} · {selected.Type}");
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
            branch = await projectTransitions.Sessions.NewAsync(conversationBinding.Conversation, conversationBinding.Path, conversationBinding.Path);
        }
        await projectTransitions.AdoptSessionBranchAsync(branch, reason);
        presentation.LoadSessionTranscript();
        Console.WriteLine($"{command[1..]}: {conversationBinding.Path ?? "(ephemeral)"}");
    }
}
