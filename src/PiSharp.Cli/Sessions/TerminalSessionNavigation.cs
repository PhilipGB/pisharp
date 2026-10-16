using PiSharp.Cli.Composition;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed class TerminalSessionNavigation(ActiveProjectRuntime projectBinding,
    ActiveConversationRuntime conversationBinding, ProjectConversationTransitions projectTransitions, TerminalPresentation presentation,
    Action<ProjectSessionRuntime> adoptProjectSession)
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

        await projectTransitions.SwitchAsync(listing.Path, adoptProjectSession, CancellationToken.None);
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
        var branch = await projectTransitions.Sessions.ForkAtUserAsync(conversationBinding.Conversation, conversationBinding.Path, id);
        conversationBinding.Adopt(branch.Conversation, branch.Run, branch.Path);
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

        var previousHead = conversation.Tree.HeadId;
        await conversationBinding.Run.SelectAsync(selected.Id);
        try
        {
            if (conversationBinding.Path is not null)
                await projectBinding.Store.SaveAsync(conversation, conversationBinding.Path);
        }
        catch
        {
            await conversationBinding.Run.SelectAsync(previousHead);
            throw;
        }

        presentation.LoadSessionTranscript();
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
        if (command == "/clone")
            branch = await projectTransitions.Sessions.CloneAsync(conversationBinding.Conversation, conversationBinding.Path);
        else
            branch = await projectTransitions.Sessions.NewAsync(conversationBinding.Conversation, conversationBinding.Path, conversationBinding.Path);
        conversationBinding.Adopt(branch.Conversation, branch.Run, branch.Path);
        presentation.LoadSessionTranscript();
        Console.WriteLine($"{command[1..]}: {conversationBinding.Path ?? "(ephemeral)"}");
    }
}
