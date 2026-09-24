using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Bounds each continuation request without changing MAF history or the application-owned raw transcript.</summary>
internal sealed class InFlightContextBudget(
    AutoCompactionPolicy policy,
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<PiAgent.CompactionSummary>> summarize,
    Func<PiAgent.CompactionSummary, CancellationToken, Task> onSummary)
{
    public async Task<IReadOnlyList<ChatMessage>> ProjectAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (AutoCompactionPolicy.Estimate(messages, "") <= policy.TriggerTokens) return messages;

        // Whole user turns are indivisible: a tool call and its results cannot be separated.
        var users = Enumerable.Range(0, messages.Count).Where(i => messages[i].Role == ChatRole.User).ToArray();
        if (users.Length < 2)
            throw new InvalidOperationException("Tool-loop context exceeds the configured budget; no completed user turn can be summarized safely.");
        var boundary = users[^1];
        if (policy.KeepRecentTokens is int keepRecent)
        {
            long estimated = 0;
            for (var i = users.Length - 1; i >= 0; i--)
            {
                var end = i + 1 < users.Length ? users[i + 1] : messages.Count;
                for (var index = users[i]; index < end; index++)
                    estimated += AutoCompactionPolicy.Estimate([messages[index]], "") - 512;
                boundary = users[i];
                if (estimated >= keepRecent) break;
            }
        }
        if (boundary <= users[0])
            throw new InvalidOperationException("Tool-loop context exceeds the configured budget; recent whole turns leave nothing safe to summarize.");

        var older = messages.Skip(users[0]).Take(boundary - users[0]).ToArray();
        var summary = await summarize(older, cancellationToken);
        await onSummary(summary, cancellationToken);
        var projected = new List<ChatMessage>(messages.Count - older.Length + 1);
        projected.AddRange(messages.Take(users[0])); // Preserve all system/developer instructions.
        projected.Add(new ChatMessage(ChatRole.User, "[Summary of earlier conversation; original turns remain in session history.]\n" + summary.Text));
        projected.AddRange(messages.Skip(boundary));
        if (AutoCompactionPolicy.Estimate(projected, "") > policy.TriggerTokens)
            throw new InvalidOperationException("Tool-loop context still exceeds the configured budget after summarization; shorten tool output or increase the model context window.");
        return projected;
    }
}
