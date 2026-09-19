using System.Text.Json;
using System.Text.Json.Serialization;

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
    public static PiSessionHeader Create(string workingDirectory, string? parentSession = null, string? id = null) =>
        new(
            "session",
            3,
            string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : ValidateId(id),
            DateTimeOffset.UtcNow,
            Path.GetFullPath(workingDirectory),
            parentSession);

    /// <summary>
    /// Pinned assertValidSessionId: the id must be non-empty, contain only alphanumeric
    /// characters, '-', '_', and '.', and start and end with an alphanumeric character.
    /// </summary>
    public static string ValidateId(string id)
    {
        // Pinned /^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9])?$/: ASCII alphanumerics only.
        if (id.Length == 0 ||
            !IsAsciiLetterOrDigit(id[0]) ||
            !IsAsciiLetterOrDigit(id[^1]) ||
            id.Any(ch => !IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.')))
        {
            throw new ArgumentException(
                "Session id must be non-empty, contain only alphanumeric characters, '-', '_', and '.', and start and end with an alphanumeric character");
        }

        return id;
    }

    private static bool IsAsciiLetterOrDigit(char ch) => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}

/// <summary>Base for every durable Pi session entry.</summary>
public abstract record SessionEntry(
    string Type,
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp);

/// <summary>A durable Pi message, including user, assistant, tool-result, and bash-execution roles.</summary>
public sealed record MessageEntry(
    string Id,
    string? ParentId,
    DateTimeOffset Timestamp,
    JsonElement Message)
    : SessionEntry(SessionEntryTypes.Message, Id, ParentId, Timestamp);

/// <summary>Legacy PiSharp-only bash entry retained for backward-compatible reads.</summary>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool FromHook = false)
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool FromHook = false)
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
/// <summary>
/// Model/thinking settings derived from a session's active entry path (pinned
/// session-manager.ts getSessionContextSettings): the last model_change entry wins, but
/// assistant messages also record the model that produced them, so a session without
/// explicit model changes still restores from its assistant history. The last
/// thinking_level_change entry wins for thinking.
/// </summary>
public sealed record SessionContextSettings(
    (string Provider, string ModelId)? Model,
    string? ThinkingLevel,
    bool HasThinkingEntry)
{
    /// <summary>Projects the model/thinking state from an active entry path.</summary>
    public static SessionContextSettings FromPath(IReadOnlyList<SessionEntry> path)
    {
        (string, string)? model = null;
        string? thinking = null;
        var hasThinkingEntry = false;

        foreach (var entry in path)
        {
            switch (entry)
            {
                case ThinkingLevelChangeEntry thinkingChange:
                    thinking = thinkingChange.ThinkingLevel;
                    hasThinkingEntry = true;
                    break;

                case ModelChangeEntry modelChange:
                    model = (modelChange.Provider, modelChange.ModelId);
                    break;

                case MessageEntry message
                    when IsAssistantWithModel(message.Message, out var provider, out var modelId):
                    model = (provider, modelId);
                    break;
            }
        }

        return new SessionContextSettings(
            model is { } m ? (m.Item1, m.Item2) : null,
            thinking,
            hasThinkingEntry);
    }

    private static bool IsAssistantWithModel(JsonElement message, out string provider, out string modelId)
    {
        provider = string.Empty;
        modelId = string.Empty;
        return message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("role", out var role)
            && string.Equals(role.GetString(), "assistant", StringComparison.Ordinal)
            && message.TryGetProperty("provider", out var providerElement)
            && providerElement.ValueKind == JsonValueKind.String
            && message.TryGetProperty("model", out var modelElement)
            && modelElement.ValueKind == JsonValueKind.String
            && (provider = providerElement.GetString()!) is { Length: > 0 }
            && (modelId = modelElement.GetString()!) is { Length: > 0 };
    }
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
