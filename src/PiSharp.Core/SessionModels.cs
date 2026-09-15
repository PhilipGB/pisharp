using System.Text.Json;

namespace PiSharp.Core;

public static class SessionEntryTypes
{
    public const string Message = "message";
    public const string BashExecution = "bash_execution";
    public const string ModelChange = "model_change";
    public const string ThinkingLevelChange = "thinking_level_change";
    public const string Compaction = "compaction";
    public const string BranchSummary = "branch_summary";
    public const string Custom = "custom";
    public const string CustomMessage = "custom_message";
    public const string Label = "label";
    public const string SessionInfo = "session_info";
    public const string AgentStateCache = "pisharp.agent-state";
}

/// <summary>Pi-compatible v3 session header.</summary>
public sealed record PiSessionHeader(
    string Type,
    int Version,
    string Id,
    DateTimeOffset Timestamp,
    string Cwd,
    string? ParentSession = null)
{
    /// <summary>Creates a new Pi-compatible session header.</summary>
    public static PiSessionHeader Create(string workingDirectory, string? parentSession = null) =>
        new(
            "session",
            3,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            Path.GetFullPath(workingDirectory),
            parentSession);
}

/// <summary>Base for every durable Pi session entry.</summary>
public abstract record SessionEntry(
    string Type,
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp);

/// <summary>A durable user, assistant, or tool-result message.</summary>
public sealed record MessageEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    JsonElement Message)
    : SessionEntry(SessionEntryTypes.Message, Id, ParentId, Timestamp);

/// <summary>A manually executed shell command and its result.</summary>
public sealed record BashExecutionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string Command,
    string Output,
    int ExitCode,
    bool ExcludeFromContext)
    : SessionEntry(SessionEntryTypes.BashExecution, Id, ParentId, Timestamp);

/// <summary>A persisted provider/model selection change.</summary>
public sealed record ModelChangeEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string Provider,
    string ModelId)
    : SessionEntry(SessionEntryTypes.ModelChange, Id, ParentId, Timestamp);

/// <summary>A persisted thinking-level selection change.</summary>
public sealed record ThinkingLevelChangeEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string ThinkingLevel)
    : SessionEntry(SessionEntryTypes.ThinkingLevelChange, Id, ParentId, Timestamp);

/// <summary>A durable context compaction result.</summary>
public sealed record CompactionEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string Summary,
    string FirstKeptEntryId,
    long TokensBefore,
    JsonElement? Details = null,
    JsonElement? Usage = null,
    bool FromHook = false)
    : SessionEntry(SessionEntryTypes.Compaction, Id, ParentId, Timestamp);

/// <summary>A summary of work abandoned while changing branches.</summary>
public sealed record BranchSummaryEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string FromId,
    string Summary,
    JsonElement? Details = null,
    JsonElement? Usage = null,
    bool FromHook = false)
    : SessionEntry(SessionEntryTypes.BranchSummary, Id, ParentId, Timestamp);

/// <summary>Extension-owned state that is not sent to the model.</summary>
public sealed record CustomEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string CustomType,
    JsonElement? Data = null)
    : SessionEntry(SessionEntryTypes.Custom, Id, ParentId, Timestamp);

/// <summary>Extension-owned content that participates in model context.</summary>
public sealed record CustomMessageEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string CustomType,
    JsonElement Content,
    JsonElement? Details,
    bool Display)
    : SessionEntry(SessionEntryTypes.CustomMessage, Id, ParentId, Timestamp);

/// <summary>A user-defined label on a session entry.</summary>
public sealed record LabelEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string TargetId,
    string? Label)
    : SessionEntry(SessionEntryTypes.Label, Id, ParentId, Timestamp);

/// <summary>Session metadata such as its display name.</summary>
public sealed record SessionInfoEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    string? Name)
    : SessionEntry(SessionEntryTypes.SessionInfo, Id, ParentId, Timestamp);

public sealed record SessionHeader(
    string Type,
    int Version,
    string SessionId,
    string WorkingDirectory,
    string Model,
    DateTimeOffset CreatedAtUtc)
{
    public static SessionHeader Create(string workingDirectory, string model) =>
        new(
            "session",
            1,
            Guid.NewGuid().ToString("N"),
            Path.GetFullPath(workingDirectory),
            model,
            DateTimeOffset.UtcNow);

    internal static SessionHeader FromPi(PiSessionHeader header) =>
        new(header.Type, header.Version, header.Id, header.Cwd, string.Empty, header.Timestamp);
}

/// <summary>Durable message and tool counts for a session.</summary>
public sealed record SessionStatistics(
    string SessionId,
    string? SessionName,
    string SessionFile,
    int UserMessages,
    int AssistantMessages,
    int ToolCalls,
    int ToolResults,
    int TotalMessages);

public sealed record SessionTurn(
    string Type,
    string Id,
    string? ParentId,
    DateTimeOffset CreatedAtUtc,
    string UserMessage,
    string AssistantMessage,
    JsonElement AgentState)
{
    public static SessionTurn Create(
        string? parentId,
        string userMessage,
        string assistantMessage,
        JsonElement agentState) =>
        new(
            "turn",
            Guid.NewGuid().ToString("N"),
            parentId,
            DateTimeOffset.UtcNow,
            userMessage,
            assistantMessage,
            agentState.Clone());
}
