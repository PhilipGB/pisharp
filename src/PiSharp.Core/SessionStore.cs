using System.Diagnostics;
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

    /// <summary>
    /// The directories this store's own scope covers: the explicit session directory alone
    /// when an override is in effect, or the canonical directory plus the legacy
    /// compatibility directory for the normal default layout. An explicit directory must
    /// never reach into <c>~/.pisharp</c>.
    /// </summary>
    private string[] ScopedDirectories => _usesExplicitSessionDir
        ? [_workspaceDirectory]
        : [_workspaceDirectory, _legacyWorkspaceDirectory];

    /// <summary>Gets the workspace (project) directory this store's sessions belong to.</summary>
    public string WorkspaceRoot => _workspaceRoot;

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
    public Task<SessionDocument> CreatePiAsync(
        CancellationToken cancellationToken = default,
        string? parentSession = null,
        string? sessionId = null)
    {
        Directory.CreateDirectory(_workspaceDirectory);
        var header = PiSessionHeader.Create(_workspaceRoot, parentSession, sessionId);
        var fileName = $"{header.Timestamp:yyyyMMddTHHmmssfffZ}_{header.Id}.jsonl";
        var path = Path.Combine(_workspaceDirectory, fileName);
        return Task.FromResult(new SessionDocument(path, header, []));
    }

    /// <summary>
    /// Creates a new Pi v3 session at an explicit path (pinned <c>_setSessionFile</c> for a
    /// missing <c>--session</c> file): the path is preserved and the file stays deferred
    /// until the first assistant message.
    /// </summary>
    public Task<SessionDocument> CreatePiAtAsync(
        string path,
        CancellationToken cancellationToken = default,
        string? parentSession = null,
        string? sessionId = null)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var header = PiSessionHeader.Create(_workspaceRoot, parentSession, sessionId);
        return Task.FromResult(new SessionDocument(fullPath, header, []));
    }

    /// <summary>Pinned normalizeSessionName: collapse every line break to a space, then trim.</summary>
    public static string SanitizeName(string name)
    {
        var collapsed = name.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return collapsed.Trim();
    }

    /// <summary>
    /// Pinned picker rename: opens the session file and appends a <c>session_info</c> entry
    /// at its active leaf. A blank name is a no-op (pinned <c>if (!next) return</c>).
    /// </summary>
    public async Task RenameSessionAsync(string path, string name, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(path, cancellationToken);
        if (!document.IsPiV3)
        {
            throw new InvalidOperationException("Only Pi v3 sessions can be renamed.");
        }

        var sanitized = SanitizeName(name);
        if (sanitized.Length == 0)
        {
            return;
        }

        var entry = new SessionInfoEntry(Guid.NewGuid().ToString("N"), document.LatestEntryId, DateTimeOffset.UtcNow, sanitized);
        await AppendEntriesAsync(document, [entry], cancellationToken);
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

        var contentLines = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();

        // Pinned _loadEntries: a Pi session file below the current version is migrated in
        // memory and the file is rewritten immediately, so the next load is already v3.
        var activeLines = contentLines;
        if (IsPiSessionHeader(contentLines[0]))
        {
            var migrated = SessionMigration.MigrateSessionLines(contentLines);
            if (migrated is not null)
            {
                await RewriteSessionFileAsync(path, migrated, cancellationToken);
                activeLines = migrated;
            }
        }

        if (IsPiSessionHeader(activeLines[0]))
        {
            var header = JsonSerializer.Deserialize<PiSessionHeader>(activeLines[0], JsonOptions)
                ?? throw new InvalidDataException($"Invalid Pi session header: {path}");
            var entries = new List<SessionEntry>();
            for (var i = 1; i < activeLines.Length; i++)
            {
                entries.Add(DeserializeEntry(activeLines[i], i + 1, path));
            }
            return new SessionDocument(path, header, entries);
        }

        var legacyHeader = JsonSerializer.Deserialize<SessionHeader>(activeLines[0], JsonOptions)
            ?? throw new InvalidDataException($"Invalid session header: {path}");
        if (!string.Equals(legacyHeader.Type, "session", StringComparison.Ordinal) || legacyHeader.Version != 1)
        {
            throw new InvalidDataException($"Unsupported session format in {path}.");
        }

        var turns = new List<SessionTurn>();
        for (var i = 1; i < activeLines.Length; i++)
        {
            var turn = JsonSerializer.Deserialize<SessionTurn>(activeLines[i], JsonOptions)
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
    /// True when the first line is a Pi session header (type <c>session</c> with a non-empty
    /// <c>id</c>), as opposed to a legacy PiSharp v1 header (which carries <c>sessionId</c>).
    /// </summary>
    private static bool IsPiSessionHeader(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return root.TryGetProperty("type", out var type) &&
               string.Equals(type.GetString(), "session", StringComparison.Ordinal) &&
               root.TryGetProperty("id", out var id) &&
               id.ValueKind == JsonValueKind.String &&
               id.GetString() is { Length: > 0 };
    }

    /// <summary>
    /// Full crash-safe rewrite of a migrated session file (pinned <c>_rewriteFile</c> truncates
    /// in place; the temp+rename replacement produces identical content without a torn file
    /// on failure).
    /// </summary>
    private static async Task RewriteSessionFileAsync(string path, string[] lines, CancellationToken cancellationToken)
    {
        var tempPath = $"{path}.migrate-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllLinesAsync(tempPath, lines, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>
    /// Lists session metadata (never full documents) for the workspace: the canonical
    /// session directory plus the legacy <c>~/.pisharp/sessions</c> workspace key directory
    /// (so pre-Phase-3 sessions stay discoverable) — or the explicit session directory
    /// alone when an override is in effect. Malformed or unreadable files are skipped; the
    /// result is sorted newest-modified first (ties by path).
    /// </summary>
    public async Task<IReadOnlyList<PiSessionInfo>> ListInfosAsync(CancellationToken cancellationToken = default)
    {
        return await ListInfosFromDirectoriesAsync(
            ScopedDirectories,
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
        // Pinned listAll(customSessionDir): an explicit session directory is the whole scope.
        if (_usesExplicitSessionDir)
        {
            return await ListInfosFromDirectoriesAsync(
                [_workspaceDirectory], Path.GetFullPath(_workspaceRoot), filterCwd: false, cancellationToken);
        }

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

    /// <summary>
    /// Result of a session file deletion (pinned deleteSessionFile): success plus the method
    /// that removed it ("trash" or "unlink") and an error when both failed.
    /// </summary>
    public sealed record SessionDeleteResult(bool Ok, string Method, string? Error);

    /// <summary>
    /// Test seam for the <c>trash</c> CLI launch; null uses the real launcher.
    /// </summary>
    internal Func<string, CancellationToken, Task<(int ExitCode, string? ErrorText)>>? TrashLauncher;

    /// <summary>
    /// Test seam for the unlink step; when set it replaces <see cref="File.Delete"/> and
    /// reports whether the file was removed (false simulates a failed unlink).
    /// </summary>
    internal Func<string, bool>? DeleteFileOverride;

    /// <summary>
    /// Pinned deleteSessionFile: tries the <c>trash</c> CLI first (adding <c>--</c> for
    /// leading-dash paths), treats a zero exit code or a disappeared file as success, and
    /// otherwise falls back to a permanent unlink.
    /// </summary>
    public async Task<SessionDeleteResult> DeleteSessionAsync(string path, CancellationToken cancellationToken = default)
    {
        string? trashError = null;
        var trashExitCode = -1;
        try
        {
            (trashExitCode, trashError) = await (TrashLauncher ?? LaunchTrashAsync)(path, cancellationToken);
        }
        catch (Exception)
        {
            // trash is not installed or not executable: fall through to the unlink path.
            trashError = "trash unavailable";
        }

        if (trashExitCode == 0 || !File.Exists(path))
        {
            return new SessionDeleteResult(true, "trash", null);
        }

        try
        {
            if (DeleteFileOverride is not null)
            {
                if (!DeleteFileOverride(path))
                {
                    throw new IOException("Simulated unlink failure");
                }
            }
            else
            {
                File.Delete(path);
            }

            return new SessionDeleteResult(true, "unlink", null);
        }
        catch (Exception ex)
        {
            var hint = string.IsNullOrWhiteSpace(trashError) ? null : $"trash: {FirstLine(trashError).Trim()}";
            return new SessionDeleteResult(false, "unlink", hint is null ? ex.Message : $"{ex.Message} ({hint})");
        }
    }

    private static string FirstLine(string text) => text.Split('\n')[0];

    private static async Task<(int ExitCode, string? ErrorText)> LaunchTrashAsync(string path, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "trash",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        // Pinned: guard leading-dash paths with --.
        if (path.Length > 0 && path[0] == '-')
        {
            process.StartInfo.ArgumentList.Add("--");
        }

        process.StartInfo.ArgumentList.Add(path);

        process.Start();
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, error);
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
        // An explicit session directory is the whole discovery scope as well; the legacy
        // compatibility directory belongs to the normal default layout only.
        foreach (var directory in ScopedDirectories)
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

        // Pinned resolveSessionPath: exact id first, then the first case-sensitive prefix
        // match (JavaScript startsWith is case-sensitive) in normal listing order.
        var infos = await ListInfosAsync(cancellationToken);
        var match = infos.FirstOrDefault(info => string.Equals(info.Id, selector, StringComparison.Ordinal))
            ?? infos.FirstOrDefault(info => info.Id.StartsWith(selector, StringComparison.Ordinal));
        if (match is not null)
        {
            return new SessionResolution(match.Path, null);
        }

        // Pinned resolveSessionPath: fall back to a global search across every project's
        // session directory; a hit carries the foreign cwd so the caller can offer to fork
        // it into the current directory instead of opening it directly.
        var all = await ListAllInfosAsync(cancellationToken);
        var global = all.FirstOrDefault(info => string.Equals(info.Id, selector, StringComparison.Ordinal))
            ?? all.FirstOrDefault(info => info.Id.StartsWith(selector, StringComparison.Ordinal));
        return global is null ? null : new SessionResolution(global.Path, global.Cwd);
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
        CancellationToken cancellationToken = default,
        string? sessionId = null)
    {
        if (!source.IsPiV3)
        {
            throw new InvalidOperationException("Pi v3 forking requires a Pi v3 source session.");
        }

        // Pinned forkFrom: a fresh session (new id, parentSession = the source file) with
        // the source's active branch up to the target entry; the source is never modified.
        var fork = await CreatePiAsync(cancellationToken, source.FilePath, sessionId);
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

    /// <summary>
    /// Pinned exportSessionToJsonl: writes the active branch as a stand-alone JSONL file —
    /// a fresh header (same id, new timestamp, no parentSession) and the branch entries
    /// re-chained sequentially (first parentId null). The default file name is
    /// <c>session-&lt;timestamp&gt;.jsonl</c> in the working directory (the pinned ISO
    /// timestamp with ':' and '.' replaced by '-'). The source session is never modified.
    /// Works from memory, so an unflushed session exports its in-memory entries.
    /// </summary>
    public async Task<string> ExportJsonlAsync(
        SessionDocument document,
        string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsPiV3)
        {
            throw new InvalidOperationException("JSONL export requires a Pi v3 session.");
        }

        var header = document.PiHeader!;
        var timestamp = DateTimeOffset.UtcNow;
        // Pinned new Date().toISOString() carries a 'Z' suffix (not an offset), so render
        // the UTC time, then swap ':' and '.' for '-'.
        var filePath = Path.GetFullPath(
            outputPath ?? $"session-{timestamp.UtcDateTime.ToString("o").Replace(':', '-').Replace('.', '-')}.jsonl",
            _workspaceRoot);
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Pinned write: LF-joined lines with a trailing newline, sequential parent chain.
        var lines = new List<string> { JsonSerializer.Serialize(
            new PiSessionHeader("session", 3, header.Id, timestamp, header.Cwd), JsonOptions) };
        string? parentId = null;
        foreach (var entry in document.GetActiveEntryPath(document.LatestEntryId))
        {
            lines.Add(JsonSerializer.Serialize(entry with { ParentId = parentId }, entry.GetType(), JsonOptions));
            parentId = entry.Id;
        }

        await File.WriteAllTextAsync(filePath, string.Join("\n", lines) + "\n", cancellationToken);
        return filePath;
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
