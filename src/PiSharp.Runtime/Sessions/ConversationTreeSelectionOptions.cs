namespace PiSharp.Runtime.Sessions;

/// <summary>Optional branch-summary behavior for selecting a conversation tree entry.</summary>
public sealed record ConversationTreeSelectionOptions(bool Summarize = false,
    string? CustomInstructions = null, bool ReplaceInstructions = false, string? Label = null,
    int ReserveTokens = AutoCompactionPolicy.DefaultReserveTokens,
    double? ConfiguredReserveTokens = null)
{
    /// <summary>File settings retain Pi's numeric coercion, including fractional and NaN budgets.</summary>
    internal double HistoryTokenBudget(int contextWindow) =>
        (contextWindow == 0 ? 128_000 : contextWindow) - (ConfiguredReserveTokens ?? ReserveTokens);
}

/// <summary>Result of navigating the session tree.</summary>
public sealed record ConversationTreeNavigationResult(string? EditorText = null);
