using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PiSharp.Core;

/// <summary>
/// Result of resolving a <c>--session</c> selector (pinned main.ts
/// <c>resolveSessionPath</c>): the session file path, and — when the match was found in
/// another project's session directory — the foreign cwd that selected it from.
/// </summary>
public sealed record SessionResolution(string Path, string? ForeignCwd);

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false,
    };

    private readonly string _workspaceRoot;
    private readonly string _workspaceDirectory;
    private readonly string _canonicalDirectory;
    private readonly string _legacyWorkspaceDirectory;
    private readonly bool _usesExplicitSessionDir;

    public SessionStore(string workspaceRoot, string? sessionsRoot = null, string? agentDirectory = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _canonicalDirectory = SessionDirectory.GetDefaultSessionDirPath(_workspaceRoot, agentDirectory);
        _usesExplicitSessionDir = !string.IsNullOrWhiteSpace(sessionsRoot);
        _workspaceDirectory = _usesExplicitSessionDir
            ? Path.GetFullPath(sessionsRoot!)
            : _canonicalDirectory;
        _legacyWorkspaceDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pisharp",
            "sessions",
            GetWorkspaceKey(_workspaceRoot));
    }

    /// <summary>Gets the directory new sessions are written to (explicit override or Pi's canonical layout).</summary>
    public string WorkspaceDirectory => _workspaceDirectory;

    /// <summary>Gets Pi's canonical encoded-cwd directory for this workspace (under the agent directory).</summary>
    public string CanonicalDirectory => _canonicalDirectory;

    /// <summary>
    /// Gets the pre-Phase-3 PiSharp session directory (<c>~/.pisharp/sessions/&lt;key&gt;/</c>).
    /// Discovered as a secondary, read-mostly compatibility source; new sessions never go there.
    /// </summary>
    public string LegacyWorkspaceDirectory => _legacyWorkspaceDirectory;

    /// <summary>True when a session directory override (CLI/env/settings) is in effect.</summary>
    public bool UsesExplicitSessionDir => _usesExplicitSessionDir;

    public async Task<SessionDocument> CreateAsync(string model, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_workspaceDirectory);
        var header = SessionHeader.Create(_workspaceRoot, model);
        var fileName = $"{header.CreatedAtUtc:yyyyMMddTHHmmssfffZ}_{header.SessionId}.jsonl";
        var path = Path.Combine(_workspaceDirectory, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(header, JsonOptions) + Environment.NewLine, cancellationToken);
        return new SessionDocument(path, header);
    }

    /// <summary>
    /// Creates a new Pi v3 session (pinned <c>newSession</c>): the header exists in memory
    /// only. The file is not written until the session's first assistant message (pinned
    /// <c>_persist</c> lazy-flush contract), so a session that never gets a response never
    /// appears in listings or on disk.
    /// </summary>
    public Task<SessionDocument> CreatePiAsync(CancellationToken cancellationToken = default, string? parentSession = null)
    {
        Directory.CreateDirectory(_workspaceDirectory);
        var header = PiSessionHeader.Create(_workspaceRoot, parentSession);
        var fileName = $"{header.Timestamp:yyyyMMddTHHmmssfffZ}_{header.Id}.jsonl";
        var path = Path.Combine(_workspaceDirectory, fileName);
        return Task.FromResult(new SessionDocument(path, header, []));
    }

    /// <summary>
    /// Creates a new Pi v3 session at an explicit path (pinned <c>_setSessionFile</c> for a
    /// missing <c>--session</c> file): the path is preserved and the file stays deferred
    /// until the first assistant message.
    /// </summary>
    public Task<SessionDocument> CreatePiAtAsync(string path, CancellationToken cancellationToken = default, string? parentSession = null)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var header = PiSessionHeader.Create(_workspaceRoot, parentSession);
        return Task.FromResult(new SessionDocument(fullPath, header, []));
    }

    public async Task AppendTurnAsync(SessionDocument document, SessionTurn turn, CancellationToken cancellationToken = default)
    {
        // Durable commit ordering: validate before writing, commit the session file,
        // and only then advance the in-memory graph. A failed commit must not leave
        // the session state ahead of the session file.
        document.ValidateTurn(turn);
        await File.AppendAllTextAsync(
            document.FilePath,
            JsonSerializer.Serialize(turn, JsonOptions) + Environment.NewLine,
            cancellationToken);
        document.Add(turn);
    }

    public async Task AppendEntriesAsync(
        SessionDocument document,
        IEnumerable<SessionEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var pending = entries.ToArray();

        // Durable commit ordering: validate the whole batch before writing, commit the
        // session file, and only then advance the in-memory graph.
        document.ValidateEntries(pending);
        if (document.IsPiV3)
        {
            await CommitPiEntriesAsync(document, pending, cancellationToken);
        }
        else
        {
            var lines = pending.Select(entry => JsonSerializer.Serialize(entry, entry.GetType(), JsonOptions));
            await File.AppendAllTextAsync(document.FilePath, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken);
        }
        document.AddEntries(pending);
    }

    /// <summary>
    /// Commits typed entries under the pinned lazy-flush contract (<c>_persist</c>): before
    /// the session's first assistant message nothing is written (a header-only session must
    /// not exist on disk); the first batch containing an assistant message materializes the
    /// whole session file (header + every entry) with exclusive creation; afterwards plain
    /// appends.
    /// </summary>
    private static async Task CommitPiEntriesAsync(SessionDocument document, SessionEntry[] pending, CancellationToken cancellationToken)
    {
        var hasAssistant = document.Entries.Any(IsAssistantMessage) || pending.Any(IsAssistantMessage);
        if (!hasAssistant)
        {
            if (document.IsFileFlushed)
            {
                // An explicitly materialized session (e.g. opened at a pre-existing path)
                // keeps receiving its pre-assistant entries as plain appends.
                await AppendLinesAsync(document, pending, cancellationToken);
            }
            return;
        }

        if (!document.IsFileFlushed)
        {
            // Pinned openSync(sessionFile, "wx"): exclusive creation — a concurrent writer
            // owns the path and the conflict surfaces instead of a merge.
            using var stream = new FileStream(
                document.FilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(JsonSerializer.Serialize(document.PiHeader!, JsonOptions));
            foreach (var entry in document.Entries.Concat(pending))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(entry, entry.GetType(), JsonOptions));
            }
            await writer.FlushAsync(cancellationToken);
            document.MarkFlushed();
            return;
        }

        await AppendLinesAsync(document, pending, cancellationToken);
    }

    private static async Task AppendLinesAsync(SessionDocument document, SessionEntry[] pending, CancellationToken cancellationToken)
    {
        var lines = pending.Select(entry => JsonSerializer.Serialize(entry, entry.GetType(), JsonOptions));
        await File.AppendAllTextAsync(document.FilePath, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken);
    }

    private static bool IsAssistantMessage(SessionEntry entry) =>
        entry is MessageEntry message &&
        message.Message.ValueKind == JsonValueKind.Object &&
        message.Message.TryGetProperty("role", out var role) &&
        string.Equals(role.GetString(), "assistant", StringComparison.Ordinal);

    public async Task<SessionDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length == 0)
        {
            throw new InvalidDataException($"Session file is empty: {path}");
        }

        using var first = JsonDocument.Parse(lines[0]);
        var root = first.RootElement;
        if (root.TryGetProperty("version", out var version) && version.GetInt32() >= 3 &&
            root.TryGetProperty("id", out _))
        {
            var header = JsonSerializer.Deserialize<PiSessionHeader>(lines[0], JsonOptions)
                ?? throw new InvalidDataException($"Invalid Pi session header: {path}");
            var entries = new List<SessionEntry>();
            for (var i = 1; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    entries.Add(DeserializeEntry(lines[i], i + 1, path));
                }
            }
            return new SessionDocument(path, header, entries);
        }

        var legacyHeader = JsonSerializer.Deserialize<SessionHeader>(lines[0], JsonOptions)
            ?? throw new InvalidDataException($"Invalid session header: {path}");
        if (!string.Equals(legacyHeader.Type, "session", StringComparison.Ordinal) || legacyHeader.Version != 1)
        {
            throw new InvalidDataException($"Unsupported session format in {path}.");
        }

        var turns = new List<SessionTurn>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var turn = JsonSerializer.Deserialize<SessionTurn>(lines[i], JsonOptions)
                ?? throw new InvalidDataException($"Invalid session entry at line {i + 1} in {path}.");
            if (!string.Equals(turn.Type, "turn", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsupported session entry type '{turn.Type}' at line {i + 1} in {path}.");
            }

            turns.Add(turn);
        }

        return new SessionDocument(path, legacyHeader, turns);
    }

    /// <summary>
    /// Lists session metadata (never full documents) for the workspace: the canonical or
    /// explicit session directory plus the legacy <c>~/.pisharp/sessions</c> workspace key
    /// directory, so pre-Phase-3 sessions stay discoverable. Malformed or unreadable files
    /// are skipped; the result is sorted newest-modified first (ties by path).
    /// </summary>
    public async Task<IReadOnlyList<PiSessionInfo>> ListInfosAsync(CancellationToken cancellationToken = default)
    {
        // The workspace's canonical (or explicit) directory plus the legacy compatibility
        // directory, so pre-Phase-3 sessions stay discoverable next to the new layout.
        return await ListInfosFromDirectoriesAsync(
            [_workspaceDirectory, _legacyWorkspaceDirectory],
            _workspaceRoot,
            filterCwd: _usesExplicitSessionDir,
            cancellationToken);
    }

    /// <summary>
    /// Lists session metadata across every project: all encoded-cwd directories under the
    /// agent sessions root plus every legacy workspace directory (pinned
    /// <c>SessionManager.listAll</c>, extended with the compatibility source).
    /// </summary>
    public async Task<IReadOnlyList<PiSessionInfo>> ListAllInfosAsync(CancellationToken cancellationToken = default)
    {
        var directories = new List<string>();
        var agentSessionsRoot = Path.GetDirectoryName(CanonicalDirectory)!;
        if (Directory.Exists(agentSessionsRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(agentSessionsRoot))
            {
                directories.Add(dir);
            }
        }

        var legacyRoot = Path.GetDirectoryName(LegacyWorkspaceDirectory)!;
        if (Directory.Exists(legacyRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(legacyRoot))
            {
                if (!directories.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    directories.Add(dir);
                }
            }
        }

        return await ListInfosFromDirectoriesAsync(
            directories, Path.GetFullPath(_workspaceRoot), filterCwd: false, cancellationToken);
    }

    private static async Task<IReadOnlyList<PiSessionInfo>> ListInfosFromDirectoriesAsync(
        IEnumerable<string> directories,
        string workspaceCwd,
        bool filterCwd,
        CancellationToken cancellationToken)
    {
        var resolvedCwd = filterCwd ? Path.GetFullPath(workspaceCwd) : null;
        var infos = new List<PiSessionInfo>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = await SessionInfoReader.ReadAsync(path, cancellationToken);
                if (info is null)
                {
                    continue;
                }

                if (resolvedCwd is not null &&
                    (info.Cwd.Length == 0 ||
                     !string.Equals(Path.GetFullPath(info.Cwd), resolvedCwd,
                         OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                {
                    continue;
                }

                infos.Add(info);
            }
        }

        return infos
            .OrderByDescending(info => info.Modified)
            .ThenBy(info => info.Path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Selects the most recent session (pinned <c>findMostRecentSession</c>): a best-effort
    /// header scan of every <c>*.jsonl</c> in the workspace directories, filtered by header
    /// cwd only when an explicit session directory is in effect (the canonical directory is
    /// already cwd-scoped), then sorted by file modification time. Returns null when there
    /// is no session — the caller creates a fresh one (pinned <c>continueRecent</c>).
    /// </summary>
    public async Task<SessionDocument?> ContinueAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new List<(string Path, DateTimeOffset Mtime)>();
        foreach (var directory in new[] { _workspaceDirectory, _legacyWorkspaceDirectory })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var header = await ReadHeaderAsync(path, cancellationToken);
                if (header is null)
                {
                    continue;
                }

                if (_usesExplicitSessionDir &&
                    (string.IsNullOrEmpty(header.Cwd) ||
                     !string.Equals(Path.GetFullPath(header.Cwd), _workspaceRoot,
                         OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
                    candidates.Add((path, File.GetLastWriteTimeUtc(path)));
                }
                catch (IOException)
                {
                    // Directory and stat races make discovery best-effort (pinned).
                }
            }
        }

        var newest = candidates
            .OrderByDescending(candidate => candidate.Mtime)
            .ThenBy(candidate => candidate.Path, StringComparer.Ordinal)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
        return newest is null ? null : await LoadAsync(newest, cancellationToken);
    }

    /// <summary>Best-effort header read for discovery: null for unreadable or non-session files.</summary>
    internal static async Task<PiSessionHeader?> ReadHeaderAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                return root.ValueKind == JsonValueKind.Object &&
                       root.TryGetProperty("type", out var type) &&
                       string.Equals(type.GetString(), "session", StringComparison.Ordinal) &&
                       root.TryGetProperty("id", out var id) &&
                       id.ValueKind == JsonValueKind.String &&
                       root.TryGetProperty("timestamp", out var timestamp) &&
                       DateTimeOffset.TryParse(timestamp.GetString(), out var parsedTimestamp) &&
                       root.TryGetProperty("cwd", out var cwd)
                    ? new PiSessionHeader(
                        "session",
                        root.TryGetProperty("version", out var version) ? version.GetInt32() : 1,
                        id.GetString()!,
                        parsedTimestamp,
                        cwd.ValueKind == JsonValueKind.String ? cwd.GetString()! : string.Empty)
                    : null;
            }
        }
        catch (Exception)
        {
            // Discovery is best-effort: one corrupt file must not hide the others.
            return null;
        }
    }

    /// <summary>
    /// Resolves a <c>--session</c> selector (pinned <c>resolveSessionPath</c>): a value that
    /// looks like a path (contains <c>/</c> or <c>\</c>, or ends in <c>.jsonl</c>) is used as
    /// a path relative to the workspace; otherwise the current project's sessions are
    /// matched by exact id first, then by id prefix (newest first, first match wins).
    /// Returns null when nothing matches (the caller then reports "no session found").
    /// </summary>
    public async Task<SessionResolution?> ResolveAsync(string selector, CancellationToken cancellationToken = default)
    {
        if (selector.Contains('/') || selector.Contains('\\') ||
            selector.EndsWith(".jsonl", StringComparison.Ordinal))
        {
            var path = Path.GetFullPath(selector, _workspaceRoot);
            return new SessionResolution(path, null);
        }

        var infos = await ListInfosAsync(cancellationToken);
        var match = infos.FirstOrDefault(info => string.Equals(info.Id, selector, StringComparison.Ordinal))
            ?? infos.FirstOrDefault(info => info.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : new SessionResolution(match.Path, null);
    }

    public async Task<IReadOnlyList<SessionDocument>> ListAsync(CancellationToken cancellationToken = default)
    {
        var documents = new List<SessionDocument>();
        foreach (var info in await ListInfosAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                documents.Add(await LoadAsync(info.Path, cancellationToken));
            }
            catch (InvalidDataException)
            {
                // Ignore malformed files in listings. Explicit loads still surface the error.
            }
        }

        return documents;
    }

    public async Task<SessionDocument> ForkAsync(
        SessionDocument source,
        string? turnId,
        string model,
        CancellationToken cancellationToken = default)
    {
        var fork = await CreateAsync(model, cancellationToken);
        foreach (var turn in source.GetActivePath(turnId))
        {
            await AppendTurnAsync(fork, turn, cancellationToken);
        }

        return fork;
    }

    public async Task<SessionDocument> ForkPiAsync(
        SessionDocument source,
        string? entryId,
        CancellationToken cancellationToken = default)
    {
        if (!source.IsPiV3)
        {
            throw new InvalidOperationException("Pi v3 forking requires a Pi v3 source session.");
        }

        var fork = await CreatePiAsync(cancellationToken, source.FilePath);
        var selectedEntryId = source.Entries
            .OfType<MessageEntry>()
            .FirstOrDefault(entry => string.Equals(entry.Id, entryId, StringComparison.OrdinalIgnoreCase) &&
                                     entry.Message.ValueKind == JsonValueKind.Object &&
                                     entry.Message.TryGetProperty("role", out var role) &&
                                     string.Equals(role.GetString(), "assistant", StringComparison.Ordinal)) is not null
            ? source.GetTurnLeafEntryId(entryId!)
            : entryId;
        var path = source.GetActiveEntryPath(selectedEntryId);
        if (path.Count > 0)
        {
            await AppendEntriesAsync(fork, path, cancellationToken);
        }
        return fork;
    }

    private static SessionEntry DeserializeEntry(string line, int lineNumber, string path)
    {
        using var document = JsonDocument.Parse(line);
        if (!document.RootElement.TryGetProperty("type", out var typeValue))
        {
            throw new InvalidDataException($"Session entry at line {lineNumber} has no type: {path}");
        }

        var type = typeValue.GetString();
        return type switch
        {
            SessionEntryTypes.Message => JsonSerializer.Deserialize<MessageEntry>(line, JsonOptions)!,
            SessionEntryTypes.BashExecution => JsonSerializer.Deserialize<BashExecutionEntry>(line, JsonOptions)!,
            SessionEntryTypes.ModelChange => JsonSerializer.Deserialize<ModelChangeEntry>(line, JsonOptions)!,
            SessionEntryTypes.ThinkingLevelChange => JsonSerializer.Deserialize<ThinkingLevelChangeEntry>(line, JsonOptions)!,
            SessionEntryTypes.Compaction => JsonSerializer.Deserialize<CompactionEntry>(line, JsonOptions)!,
            SessionEntryTypes.BranchSummary => JsonSerializer.Deserialize<BranchSummaryEntry>(line, JsonOptions)!,
            SessionEntryTypes.Custom => JsonSerializer.Deserialize<CustomEntry>(line, JsonOptions)!,
            SessionEntryTypes.CustomMessage => JsonSerializer.Deserialize<CustomMessageEntry>(line, JsonOptions)!,
            SessionEntryTypes.Label => JsonSerializer.Deserialize<LabelEntry>(line, JsonOptions)!,
            SessionEntryTypes.SessionInfo => JsonSerializer.Deserialize<SessionInfoEntry>(line, JsonOptions)!,
            _ => throw new InvalidDataException($"Unsupported session entry type '{type}' at line {lineNumber} in {path}."),
        };
    }

    private static string GetWorkspaceKey(string workspaceRoot)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspaceRoot));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "root";
        }

        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceRoot))).ToLowerInvariant()[..12];
        return $"{safe}-{hash}";
    }
}
