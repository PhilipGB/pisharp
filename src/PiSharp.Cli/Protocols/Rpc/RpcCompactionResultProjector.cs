using System.Text.Json.Serialization;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal static class RpcCompactionResultProjector
{
    public static object Project(ConversationCompactionResult result) => new RpcCompactionResult(
        result.Summary,
        result.FirstKeptEntryId,
        result.TokensBefore,
        result.EstimatedTokensAfter,
        result.Usage is null ? null : RpcCompactionUsage.From(result.Usage),
        result.Details);

    public static object ProjectStart(string reason) => new { type = "compaction_start", reason };

    public static object ProjectEnd(AgentLifecycleEvent item) => new RpcCompactionEndEvent(
        item.CompactionReason ?? "manual",
        item.CompactionResult is null ? null : Project(item.CompactionResult),
        item.CompactionAborted ?? false,
        item.CompactionWillRetry ?? false,
        item.Error);

    private sealed record RpcCompactionEndEvent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Result,
        [property: JsonPropertyName("aborted")] bool Aborted,
        [property: JsonPropertyName("willRetry")] bool WillRetry,
        [property: JsonPropertyName("errorMessage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorMessage)
    {
        public RpcCompactionEndEvent(string reason, object? result, bool aborted, bool willRetry, string? errorMessage)
            : this("compaction_end", reason, result, aborted, willRetry, errorMessage) { }
    }

    private sealed record RpcCompactionResult(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("firstKeptEntryId")] string FirstKeptEntryId,
        [property: JsonPropertyName("tokensBefore")] int TokensBefore,
        [property: JsonPropertyName("estimatedTokensAfter")] int EstimatedTokensAfter,
        [property: JsonPropertyName("usage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RpcCompactionUsage? Usage,
        [property: JsonPropertyName("details")] ConversationCompactionDetails Details);

    private sealed record RpcCompactionUsage(
        [property: JsonPropertyName("input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Input,
        [property: JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Output,
        [property: JsonPropertyName("cacheRead"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheRead,
        [property: JsonPropertyName("cacheWrite"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheWrite,
        [property: JsonPropertyName("reasoning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? Reasoning,
        [property: JsonPropertyName("totalTokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TotalTokens,
        [property: JsonPropertyName("cost"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RpcCompactionCost? Cost)
    {
        public static RpcCompactionUsage From(UsageRecord usage) => new(
            usage.InputTokens,
            usage.OutputTokens,
            usage.CachedInputTokens,
            usage.CachedWriteTokens,
            usage.ReasoningTokens,
            usage.TotalTokens,
            usage.Cost is null ? null : new RpcCompactionCost(
                usage.InputCost,
                usage.OutputCost,
                usage.CachedInputCost,
                usage.CachedWriteCost,
                usage.Cost.Value));
    }

    private sealed record RpcCompactionCost(
        [property: JsonPropertyName("input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Input,
        [property: JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? Output,
        [property: JsonPropertyName("cacheRead"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CacheRead,
        [property: JsonPropertyName("cacheWrite"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CacheWrite,
        [property: JsonPropertyName("total")] decimal Total);
}
