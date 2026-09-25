using System.Text.Json.Serialization;

namespace PiSharp.Runtime.Sessions;

/// <summary>Application-owned execution events shared by terminal, JSON/RPC and .NET callers.</summary>
public sealed record AgentLifecycleEvent(string Type, string? Text = null, string? Tool = null,
    string? OperationId = null, bool? IsError = null, string? Error = null,
    long? InputTokens = null, long? OutputTokens = null, long? CachedInputTokens = null,
    long? ReasoningTokens = null, long? TotalTokens = null, decimal? Cost = null,
    long? CachedWriteTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Details = null);
