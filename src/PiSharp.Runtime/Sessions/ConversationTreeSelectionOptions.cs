namespace PiSharp.Runtime.Sessions;

/// <summary>Optional branch-summary behavior for selecting a conversation tree entry.</summary>
public sealed record ConversationTreeSelectionOptions(bool Summarize = false,
    string? CustomInstructions = null, bool ReplaceInstructions = false, string? Label = null,
    int ReserveTokens = AutoCompactionPolicy.DefaultReserveTokens);

/// <summary>Result of navigating the session tree.</summary>
public sealed record ConversationTreeNavigationResult(string? EditorText = null);
