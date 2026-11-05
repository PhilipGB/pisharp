using System.Globalization;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Adapts the reusable list overlay to project-scoped and all-project saved sessions.</summary>
internal sealed class TerminalSessionPicker(ConversationStore store, TerminalEditor editor)
{
    public async Task<SessionListing?> ShowAsync(string? currentPath = null,
        ConversationSession? currentConversation = null, CancellationToken cancellationToken = default)
    {
        var state = await TerminalSessionPickerState.LoadAsync(store, currentPath, currentConversation,
            cancellationToken);
        var current = state.CurrentEntries.FirstOrDefault(entry => currentPath is not null &&
            SessionCatalog.PathsEqual(entry.Listing.Path, currentPath));
        var selectedKey = current?.Listing.CanonicalPath;
        var options = state.BuildEntries().Select(state.BuildOption).ToArray();
        var chosen = await editor.ShowSelectionListAsync("Resume Session (Current Folder)", options,
            selectedKey, scopedOptions: options, allLabel: "All", scopedLabel: "Current Folder",
            emptyMessage: "No saved sessions in this project",
            actionHint: ActionHint(state),
            handleInput: async (list, input) =>
                await HandleInputAsync(state, list, input, currentPath, currentConversation, cancellationToken),
            cancellationToken: cancellationToken);
        return chosen?.Option.Value.Listing;
    }

    internal async Task<bool> HandleInputAsync(TerminalSessionPickerState state, TerminalSelectionList<SessionPickerEntry> list,
        TerminalInputEvent input, string? currentPath, ConversationSession? currentConversation,
        CancellationToken cancellationToken)
    {
        if (input.Key is not { } key) return false;

        if (state.ConfirmingDeleteKey is not null)
        {
            if (editor.MatchesKeybinding("tui.select.confirm", key))
            {
                var deleted = await state.ConfirmDeleteAsync(cancellationToken);
                ApplyState(state, list, resetIndex: deleted);
                return true;
            }
            if (editor.MatchesKeybinding("tui.select.cancel", key))
            {
                state.CancelDelete();
                ApplyState(state, list);
                return true;
            }
            // Pinned Pi intercepts every other key while confirming deletion.
            return true;
        }

        if (editor.MatchesKeybinding("app.session.deleteNoninvasive", key))
        {
            if (ShouldForwardNoninvasiveDelete(list.SearchQuery))
            {
                // Pinned Pi forwards the key to its search Input. Under the Kitty protocol a
                // modified Backspace does not match any editor binding there, so it is ignored;
                // an ordinary Backspace still edits the query.
                if (!(key.Key == ConsoleKey.Backspace && key.Modifiers != 0)) list.HandleInput(input);
                return true;
            }
            state.BeginDelete(list.Selected?.Option.Value.Key);
            ApplyState(state, list);
            return true;
        }
        if (editor.MatchesKeybinding("app.session.delete", key))
        {
            state.BeginDelete(list.Selected?.Option.Value.Key);
            ApplyState(state, list);
            return true;
        }
        if (editor.MatchesKeybinding("app.session.toggleSort", key))
        {
            state.ToggleSort();
            ApplyState(state, list, preserveIndex: true);
            return true;
        }
        if (editor.MatchesKeybinding("app.session.toggleNamedFilter", key))
        {
            state.ToggleNameFilter();
            ApplyState(state, list, preserveIndex: true);
            return true;
        }
        if (editor.MatchesKeybinding("app.session.togglePath", key))
        {
            state.TogglePath();
            ApplyState(state, list);
            return true;
        }
        if (editor.MatchesKeybinding("app.session.rename", key))
        {
            if (list.Selected?.Option.Value is { } selected)
                await RenameAsync(state, list, selected, currentPath, currentConversation, cancellationToken);
            return true;
        }
        if (editor.MatchesKeybinding("tui.input.tab", key))
        {
            await state.EnsureAllLoadedAsync(cancellationToken);
            state.ToggleScope();
            ApplyState(state, list);
            // The list owns the scope toggle so its IsScoped state and labels stay authoritative.
            list.HandleInput(input);
            return true;
        }
        return false;
    }

    private async Task RenameAsync(TerminalSessionPickerState state, TerminalSelectionList<SessionPickerEntry> list,
        SessionPickerEntry selected, string? currentPath, ConversationSession? currentConversation,
        CancellationToken cancellationToken)
    {
        var entered = await editor.PromptSequenceAsync("Rename Session",
            [("Session name", "A short descriptive name")], cancellationToken: cancellationToken,
            initialValues: [selected.Listing.Name ?? ""]);
        if (entered is null) return;
        if (entered.Count != 1)
        {
            state.SetStatus("Failed to rename: a session name is required", error: true);
            ApplyState(state, list);
            return;
        }
        var renamed = await state.RenameAsync(selected.Key, entered[0], cancellationToken);
        ApplyState(state, list, resetIndex: renamed);
    }

    private void ApplyState(TerminalSessionPickerState state, TerminalSelectionList<SessionPickerEntry> list,
        bool preserveIndex = false, bool resetIndex = false)
    {
        var currentOptions = state.BuildEntries().Select(state.BuildOption).ToArray();
        var allOptions = state.HasLoadedAll
            ? state.BuildEntriesForScope(SessionPickerScope.All).Select(state.BuildOption).ToArray()
            : [];
        list.SortMatchesByScore = state.Sort != SessionPickerSortMode.Recent;
        list.ReplaceOptions(allOptions, currentOptions, preserveIndex);
        if (resetIndex) list.SelectIndex(0);
        list.SetTitle(state.BaseTitle);
        list.SetActionHint(ActionHint(state));
        list.SetEmptyMessage(state.NameFilter == SessionPickerNameFilter.Named
            ? "No named sessions found" : state.Scope == SessionPickerScope.All
                ? "No sessions found" : "No saved sessions in this project");
    }

    private string? ActionHint(TerminalSessionPickerState state) =>
        BuildActionHint(state, editor.GetDisplayKeys);

    internal static string? BuildActionHint(TerminalSessionPickerState state, Func<string, string?>? displayKeys)
    {
        if (state.Status is { Length: > 0 } status) return status;
        if (state.ConfirmingDeleteKey is { } key)
        {
            var confirming = state.FindEntry(key);
            var name = confirming is null ? key[..Math.Min(12, key.Length)] :
                confirming.Listing.Name ?? confirming.Listing.Id[..Math.Min(12, confirming.Listing.Id.Length)];
            // A configured-but-disabled binding renders an empty key, exactly like pinned Pi's
            // keyHint(). Only callers without a keymap at all use the default labels.
            var confirm = displayKeys is null ? "Enter" : displayKeys("tui.select.confirm") ?? "";
            var cancel = displayKeys is null ? "Escape" : displayKeys("tui.select.cancel") ?? "";
            return $"Delete {name}? {confirm} confirm · {cancel} cancel";
        }
        var parts = new List<string>
        {
            $"Sort: {state.SortLabel}",
            $"Name: {state.NameFilterLabel}",
            $"path {(state.ShowPath ? "(on)" : "(off)")}"
        };
        Add("app.session.toggleSort", "sort");
        Add("app.session.toggleNamedFilter", "named");
        Add("app.session.delete", "delete");
        Add("app.session.rename", "rename");
        Add("app.session.togglePath", "path");
        return string.Join(" · ", parts);

        void Add(string action, string label)
        {
            if (displayKeys?.Invoke(action) is { Length: > 0 } key) parts.Add($"{key} {label}");
        }
    }

    internal static TerminalSelectionOption<SessionListing> ToOption(SessionListing session, string? currentPath,
        SessionPickerBranchHeads branchHeads)
    {
        var isCurrent = currentPath is not null && SessionCatalog.PathsEqual(session.Path, currentPath);
        var id = session.Id[..Math.Min(12, session.Id.Length)];
        var name = string.IsNullOrWhiteSpace(session.Name) ? "(unnamed)" : session.Name;
        var modified = session.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var branchDescription = branchHeads.IsAvailable
            ? $"head {(branchHeads.HeadId is null ? "(empty)" : branchHeads.HeadId[..Math.Min(12, branchHeads.HeadId.Length)])} · {branchHeads.Count} branch heads"
            : "branch details unavailable";
        var description = $"{(isCurrent ? "Current · " : "")}{branchDescription} · {session.Model} · {session.MessageCount} messages · {modified}";
        var searchText = $"{name} {session.Id} {session.Model} {session.Path} {branchHeads.HeadId}";
        return new(session.CanonicalPath, session, $"{name} · {id}", description, searchText, isCurrent);
    }

    internal static bool ShouldForwardNoninvasiveDelete(string query) => query.Length > 0;

    internal static bool ShouldEnterDeleteConfirmation(string query) => query.Length == 0;
}

internal sealed record SessionPickerBranchHeads(string? HeadId, int Count, bool IsAvailable = true)
{
    public static SessionPickerBranchHeads From(ConversationSession conversation)
    {
        var entries = conversation.Tree.Entries;
        var parents = entries.Where(entry => entry.ParentId is not null)
            .Select(entry => entry.ParentId!).ToHashSet(StringComparer.Ordinal);
        return new(conversation.Tree.HeadId, entries.Count(entry => !parents.Contains(entry.Id)));
    }
}
