using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>Why the host is replacing the active session.</summary>
public enum PiSharpExtensionSessionSwitchReason
{
    /// <summary>A new session is being created.</summary>
    New,
    /// <summary>An existing session is being resumed.</summary>
    Resume
}

/// <summary>Which side of a user entry a fork begins at.</summary>
public enum PiSharpExtensionForkPosition
{
    /// <summary>Fork immediately before the selected entry.</summary>
    Before,
    /// <summary>Fork including the selected entry.</summary>
    At
}

/// <summary>Detached identity and ancestry metadata for one canonical session-tree entry.</summary>
public sealed record PiSharpExtensionSessionBranchEntry(
    string Id, string? ParentId, string Type, DateTimeOffset Timestamp);

/// <summary>Request to replace the current session, before the host commits the transition.</summary>
public sealed record PiSharpExtensionSessionBeforeSwitchEvent(
    string CurrentSessionId,
    string? CurrentSessionFile,
    PiSharpExtensionSessionSwitchReason Reason,
    string? TargetSessionFile);

/// <summary>Controls whether a session replacement proceeds.</summary>
public sealed record PiSharpExtensionSessionBeforeSwitchResult(bool Cancel = false);

/// <summary>Request to fork the active session at a canonical entry.</summary>
public sealed record PiSharpExtensionSessionBeforeForkEvent(
    string CurrentSessionId,
    string? CurrentSessionFile,
    string EntryId,
    PiSharpExtensionForkPosition Position);

/// <summary>Controls whether a fork proceeds.</summary>
public sealed record PiSharpExtensionSessionBeforeForkResult(bool Cancel = false);

/// <summary>Request to navigate to another point in the active session tree.</summary>
public sealed record PiSharpExtensionSessionBeforeTreeEvent
{
    /// <summary>Creates a detached navigation snapshot.</summary>
    public PiSharpExtensionSessionBeforeTreeEvent(string? targetEntryId, string? oldLeafEntryId,
        string? commonAncestorEntryId, IEnumerable<PiSharpExtensionSessionBranchEntry> entriesToSummarize,
        bool userWantsSummary)
    {
        ArgumentNullException.ThrowIfNull(entriesToSummarize);
        TargetEntryId = targetEntryId;
        OldLeafEntryId = oldLeafEntryId;
        CommonAncestorEntryId = commonAncestorEntryId;
        EntriesToSummarize = Array.AsReadOnly(entriesToSummarize.ToArray());
        UserWantsSummary = userWantsSummary;
    }

    /// <summary>The selected canonical entry, or null for an empty tree.</summary>
    public string? TargetEntryId { get; }
    /// <summary>The currently selected canonical leaf, or null for an empty tree.</summary>
    public string? OldLeafEntryId { get; }
    /// <summary>The common ancestor of the current and target paths, if one exists.</summary>
    public string? CommonAncestorEntryId { get; }
    /// <summary>Detached entry metadata the host would summarize. This milestone does not request summaries.</summary>
    public IReadOnlyList<PiSharpExtensionSessionBranchEntry> EntriesToSummarize { get; }
    /// <summary>Whether the host has requested a branch summary.</summary>
    public bool UserWantsSummary { get; }
}

/// <summary>Controls whether tree navigation proceeds.</summary>
public sealed record PiSharpExtensionSessionBeforeTreeResult(bool Cancel = false);

/// <summary>Notification that a tree navigation has been selected and persisted.</summary>
public sealed record PiSharpExtensionSessionTreeEvent(string? OldLeafEntryId, string? NewLeafEntryId);

/// <summary>Detached file-operation metadata used to explain a compaction plan.</summary>
public sealed record PiSharpExtensionCompactionDetails
{
    /// <summary>Creates detached file-operation metadata.</summary>
    public PiSharpExtensionCompactionDetails(IEnumerable<string> readFiles, IEnumerable<string> modifiedFiles)
    {
        ArgumentNullException.ThrowIfNull(readFiles);
        ArgumentNullException.ThrowIfNull(modifiedFiles);
        ReadFiles = Array.AsReadOnly(readFiles.ToArray());
        ModifiedFiles = Array.AsReadOnly(modifiedFiles.ToArray());
    }

    /// <summary>Files read by summarized tool calls.</summary>
    public IReadOnlyList<string> ReadFiles { get; }
    /// <summary>Files written or edited by summarized tool calls.</summary>
    public IReadOnlyList<string> ModifiedFiles { get; }
}

/// <summary>Read-only detached preparation for a canonical compaction.</summary>
public sealed record PiSharpExtensionCompactionPreparation
{
    private readonly ChatMessage[] _messagesToSummarize;
    private readonly ChatMessage[] _turnPrefixMessages;

    /// <summary>Creates a detached snapshot of the actual compaction plan.</summary>
    public PiSharpExtensionCompactionPreparation(string firstKeptEntryId,
        IEnumerable<ChatMessage> messagesToSummarize, IEnumerable<ChatMessage> turnPrefixMessages,
        bool isSplitTurn, int tokensBefore, string? previousSummary,
        PiSharpExtensionCompactionDetails? fileOperations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstKeptEntryId);
        ArgumentNullException.ThrowIfNull(messagesToSummarize);
        ArgumentNullException.ThrowIfNull(turnPrefixMessages);
        if (tokensBefore < 0) throw new ArgumentOutOfRangeException(nameof(tokensBefore));
        FirstKeptEntryId = firstKeptEntryId;
        _messagesToSummarize = messagesToSummarize.Select(PiSharpExtensionEventSnapshots.CloneMessage).ToArray();
        _turnPrefixMessages = turnPrefixMessages.Select(PiSharpExtensionEventSnapshots.CloneMessage).ToArray();
        IsSplitTurn = isSplitTurn;
        TokensBefore = tokensBefore;
        PreviousSummary = previousSummary;
        FileOperations = fileOperations;
    }

    /// <summary>The first canonical entry retained by the planned compaction.</summary>
    public string FirstKeptEntryId { get; }
    /// <summary>Detached messages the normal summarizer would receive.</summary>
    public IReadOnlyList<ChatMessage> MessagesToSummarize => PiSharpExtensionEventSnapshots.SnapshotMessages(_messagesToSummarize);
    /// <summary>Detached messages used to preserve the prefix of a split turn.</summary>
    public IReadOnlyList<ChatMessage> TurnPrefixMessages => PiSharpExtensionEventSnapshots.SnapshotMessages(_turnPrefixMessages);
    /// <summary>Whether the active turn is split across the compaction boundary.</summary>
    public bool IsSplitTurn { get; }
    /// <summary>Estimated context token count before compaction.</summary>
    public int TokensBefore { get; }
    /// <summary>Prior summary supplied to the normal summarizer, when available.</summary>
    public string? PreviousSummary { get; }
    /// <summary>Read-only file-operation details derived from the planned history.</summary>
    public PiSharpExtensionCompactionDetails? FileOperations { get; }
}

/// <summary>Usage metadata supported for an extension-supplied compaction.</summary>
public sealed record PiSharpExtensionCompactionUsage(long InputTokens, long OutputTokens,
    long CachedInputTokens = 0, long ReasoningTokens = 0, long TotalTokens = 0, long CachedWriteTokens = 0);

/// <summary>Extension-supplied summary content for a canonical compaction entry.</summary>
public sealed record PiSharpExtensionCustomCompaction
{
    private readonly JsonElement? _details;

    /// <summary>Creates a custom compaction candidate. The host validates it against the captured branch.</summary>
    public PiSharpExtensionCustomCompaction(string summary, string firstKeptEntryId, int tokensBefore,
        JsonElement? details = null, PiSharpExtensionCompactionUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstKeptEntryId);
        if (tokensBefore < 0) throw new ArgumentOutOfRangeException(nameof(tokensBefore));
        if (details is { ValueKind: JsonValueKind.Undefined })
            throw new ArgumentException("Details must be a JSON value when supplied.", nameof(details));
        Summary = summary;
        FirstKeptEntryId = firstKeptEntryId;
        TokensBefore = tokensBefore;
        _details = details?.Clone();
        Usage = usage;
    }

    /// <summary>The summary to persist in the canonical compaction entry.</summary>
    public string Summary { get; }
    /// <summary>The retained entry ID, which must be a safe boundary on the captured active branch.</summary>
    public string FirstKeptEntryId { get; }
    /// <summary>The estimated context size captured before compaction.</summary>
    public int TokensBefore { get; }
    /// <summary>Detached JSON details to persist with the canonical compaction entry.</summary>
    public JsonElement? Details => _details?.Clone();
    /// <summary>Optional provider usage with non-negative token counts.</summary>
    public PiSharpExtensionCompactionUsage? Usage { get; }
}

/// <summary>Information supplied before canonical session compaction.</summary>
public sealed record PiSharpExtensionSessionBeforeCompactEvent
{
    /// <summary>Creates a detached pre-compaction event.</summary>
    public PiSharpExtensionSessionBeforeCompactEvent(PiSharpExtensionCompactionReason reason, bool willRetry,
        PiSharpExtensionCompactionPreparation preparation,
        IEnumerable<PiSharpExtensionSessionBranchEntry> branchEntries, string? customInstructions)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(branchEntries);
        Reason = reason;
        WillRetry = willRetry;
        Preparation = preparation;
        BranchEntries = Array.AsReadOnly(branchEntries.ToArray());
        CustomInstructions = customInstructions;
    }

    /// <summary>Why compaction was requested.</summary>
    public PiSharpExtensionCompactionReason Reason { get; }
    /// <summary>Whether a failed provider turn will be retried after compaction.</summary>
    public bool WillRetry { get; }
    /// <summary>The actual detached plan produced by the canonical coordinator.</summary>
    public PiSharpExtensionCompactionPreparation Preparation { get; }
    /// <summary>Detached metadata for the captured active branch.</summary>
    public IReadOnlyList<PiSharpExtensionSessionBranchEntry> BranchEntries { get; }
    /// <summary>Manual compaction focus instructions, when supplied.</summary>
    public string? CustomInstructions { get; }
}

/// <summary>Controls whether compaction proceeds and may provide its summary.</summary>
public sealed record PiSharpExtensionSessionBeforeCompactResult(bool Cancel = false,
    PiSharpExtensionCustomCompaction? Compaction = null);

/// <summary>Handles a request to replace the active session.</summary>
public delegate ValueTask<PiSharpExtensionSessionBeforeSwitchResult?> PiSharpExtensionSessionBeforeSwitchHandler(
    PiSharpExtensionSessionBeforeSwitchEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles a request to fork the active session.</summary>
public delegate ValueTask<PiSharpExtensionSessionBeforeForkResult?> PiSharpExtensionSessionBeforeForkHandler(
    PiSharpExtensionSessionBeforeForkEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Handles a request to navigate the active session tree.</summary>
public delegate ValueTask<PiSharpExtensionSessionBeforeTreeResult?> PiSharpExtensionSessionBeforeTreeHandler(
    PiSharpExtensionSessionBeforeTreeEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);

/// <summary>Observes committed session-tree navigation.</summary>
public delegate ValueTask PiSharpExtensionSessionTreeHandler(PiSharpExtensionSessionTreeEvent eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>Handles a request to compact the active session.</summary>
public delegate ValueTask<PiSharpExtensionSessionBeforeCompactResult?> PiSharpExtensionSessionBeforeCompactHandler(
    PiSharpExtensionSessionBeforeCompactEvent eventData, PiSharpExtensionContext context,
    CancellationToken cancellationToken);
