using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Bounds each continuation request without changing MAF history or the application-owned raw transcript.</summary>
internal sealed class InFlightContextBudget(
    AutoCompactionPolicy policy,
    Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<PiAgent.CompactionSummary>> summarize,
    Func<PiAgent.CompactionSummary, CancellationToken, Task> onSummary)
{
    // Fingerprint serialized earlier turns without retaining their raw content in the cache.
    // Fail closed to a fresh summary when one message or the aggregate is too large to fingerprint.
    private string? _cachedPrefix;
    private string? _cachedSummary;

    public async Task<IReadOnlyList<ChatMessage>> ProjectAsync(IReadOnlyList<ChatMessage> messages, bool force, CancellationToken cancellationToken)
    {
        if (!force && AutoCompactionPolicy.Estimate(messages, "") <= policy.TriggerTokens) return messages;

        // Prefer whole-turn cuts. If the current turn alone is too large, only cut after
        // fully matched tool calls/results so the provider never sees an orphaned result.
        var users = Enumerable.Range(0, messages.Count).Where(i => messages[i].Role == ChatRole.User).ToArray();
        if (users.Length == 0)
            throw new InvalidOperationException("Tool-loop context exceeds the configured budget; no user turn can be summarized safely.");
        var boundary = users[^1];
        var splitTurn = users.Length == 1 ||
            AutoCompactionPolicy.Estimate(messages.Skip(boundary).ToArray(), "") > policy.TriggerTokens;
        if (!splitTurn && policy.KeepRecentTokens is int keepRecent)
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
        if (splitTurn)
        {
            boundary = CompletedToolBoundary(messages, users[^1]);
            if (boundary <= users[^1])
                throw new InvalidOperationException("Tool-loop context exceeds the configured budget; no completed tool call/result pair can be summarized safely.");
        }
        if (boundary <= users[0])
            throw new InvalidOperationException("Tool-loop context exceeds the configured budget; recent whole turns leave nothing safe to summarize.");

        var older = messages.Skip(users[0]).Take(boundary - users[0]).ToArray();
        if (!HasBalancedToolCalls(messages, users[0], boundary))
            throw new InvalidOperationException("Tool-loop context cannot summarize an incomplete or mismatched tool call/result graph.");
        var prefix = CacheKey(older);
        var cached = prefix is not null && string.Equals(prefix, _cachedPrefix, StringComparison.Ordinal);
        string summaryText;
        if (cached)
            summaryText = _cachedSummary!;
        else
        {
            var summary = await summarize(older, cancellationToken);
            await onSummary(summary, cancellationToken);
            summaryText = summary.Text;
        }
        var projected = new List<ChatMessage>(messages.Count - older.Length + 1);
        projected.AddRange(messages.Take(users[0])); // Preserve all system/developer instructions.
        projected.Add(new ChatMessage(ChatRole.User, "[Summary of earlier conversation; original turns remain in session history.]\n" + summaryText));
        projected.AddRange(messages.Skip(boundary));
        if (AutoCompactionPolicy.Estimate(projected, "") > policy.TriggerTokens)
            throw new InvalidOperationException("Tool-loop context still exceeds the configured budget after summarization; shorten tool output or increase the model context window.");
        if (!cached)
        {
            _cachedPrefix = prefix;
            _cachedSummary = prefix is null ? null : summaryText;
        }
        return projected;
    }

    private static bool HasBalancedToolCalls(IReadOnlyList<ChatMessage> messages, int start, int end)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = start; i < end; i++)
        {
            foreach (var call in messages[i].Contents.OfType<FunctionCallContent>())
                if (string.IsNullOrEmpty(call.CallId) || !used.Add(call.CallId) || !pending.Add(call.CallId)) return false;
            foreach (var result in messages[i].Contents.OfType<FunctionResultContent>())
                if (!pending.Remove(result.CallId)) return false;
        }
        return pending.Count == 0;
    }

    private static int CompletedToolBoundary(IReadOnlyList<ChatMessage> messages, int start)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var boundary = -1;
        for (var i = start + 1; i < messages.Count; i++)
        {
            foreach (var call in messages[i].Contents.OfType<FunctionCallContent>())
                if (!pending.Add(call.CallId)) return -1;
            var results = messages[i].Contents.OfType<FunctionResultContent>().ToArray();
            foreach (var result in results)
                if (!pending.Remove(result.CallId)) return -1;
            if (results.Length > 0 && pending.Count == 0) boundary = i + 1;
        }
        // Never project a dangling call without its result, even if an earlier cut was valid.
        return pending.Count == 0 ? boundary : -1;
    }

    private static string? CacheKey(IReadOnlyList<ChatMessage> messages)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var total = 0;
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var message in messages)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, AIJsonUtilities.DefaultOptions);
            if (json.Length > 1024 * 1024 || total + (long)json.Length > 16 * 1024 * 1024) return null;
            total += json.Length;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, json.Length);
            hash.AppendData(length);
            hash.AppendData(json);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
