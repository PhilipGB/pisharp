using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Counts authoritative session records; provider-billed usage is unknown until explicitly recorded.</summary>
public sealed record SessionStatistics(string Id, string? Name, string Model, int Entries, int Leaves,
    int ActiveMessages, int UserTurns, int AssistantMessages, int ToolCalls, int ToolResults,
    int EstimatedContextTokens, int? BilledTokens, decimal? Cost)
{
    public static SessionStatistics Calculate(ConversationSession session)
    {
        var messages = session.ActiveMessages();
        var parents = session.Tree.Entries.Where(entry => entry.ParentId is not null)
            .Select(entry => entry.ParentId).ToHashSet(StringComparer.Ordinal);
        var leaves = session.Tree.Entries.Count(entry => !parents.Contains(entry.Id));
        return new(session.Id, session.Name, session.Model, session.Tree.Entries.Count, leaves,
            messages.Count, messages.Count(message => message.Role == ChatRole.User),
            messages.Count(message => message.Role == ChatRole.Assistant),
            messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Count(),
            messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count(),
            AutoCompactionPolicy.Estimate(session.ContextMessages(), ""), null, null);
    }
}
