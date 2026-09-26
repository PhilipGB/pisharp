using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Application-owned execution events shared by terminal, JSON/RPC and .NET callers.</summary>
public sealed record AgentLifecycleEvent(string Type, string? Text = null, string? Tool = null,
    string? OperationId = null, bool? IsError = null, string? Error = null,
    long? InputTokens = null, long? OutputTokens = null, long? CachedInputTokens = null,
    long? ReasoningTokens = null, long? TotalTokens = null, decimal? Cost = null,
    long? CachedWriteTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Details = null)
{
    [JsonIgnore]
    public IReadOnlyDictionary<string, object?>? ToolArguments { get; init; }

    [JsonIgnore]
    public IReadOnlyList<DataContent>? Images { get; init; }

    [JsonIgnore]
    public string? RunStartHead { get; init; }

    [JsonIgnore]
    public int? RunStartPathLength { get; init; }

    [JsonIgnore]
    public string? TurnEndHead { get; init; }

    [JsonPropertyName("attempt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAttempt { get; init; }

    [JsonPropertyName("maxAttempts"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryMaxAttempts { get; init; }

    [JsonPropertyName("delayMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RetryDelayMs { get; init; }

    [JsonPropertyName("willRetry"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WillRetry { get; init; }

    [JsonPropertyName("success"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RetrySuccess { get; init; }

    [JsonPropertyName("finalError"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetryFinalError { get; init; }
}
