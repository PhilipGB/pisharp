using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PiSharp.Core;

public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        WriteIndented = false,
    };

    private readonly string _workspaceRoot;
    private readonly string _workspaceDirectory;

    public SessionStore(string workspaceRoot, string? sessionsRoot = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        sessionsRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pisharp",
            "sessions");
        _workspaceDirectory = Path.Combine(Path.GetFullPath(sessionsRoot), GetWorkspaceKey(_workspaceRoot));
    }

    public string WorkspaceDirectory => _workspaceDirectory;

    public async Task<SessionDocument> CreateAsync(string model, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_workspaceDirectory);
        var header = SessionHeader.Create(_workspaceRoot, model);
        var fileName = $"{header.CreatedAtUtc:yyyyMMddTHHmmssfffZ}_{header.SessionId}.jsonl";
        var path = Path.Combine(_workspaceDirectory, fileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(header, JsonOptions) + Environment.NewLine, cancellationToken);
        return new SessionDocument(path, header);
    }

    public async Task AppendTurnAsync(SessionDocument document, SessionTurn turn, CancellationToken cancellationToken = default)
    {
        document.Add(turn);
        await File.AppendAllTextAsync(
            document.FilePath,
            JsonSerializer.Serialize(turn, JsonOptions) + Environment.NewLine,
            cancellationToken);
    }

    public async Task<SessionDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        if (lines.Length == 0)
        {
            throw new InvalidDataException($"Session file is empty: {path}");
        }

        var header = JsonSerializer.Deserialize<SessionHeader>(lines[0], JsonOptions)
            ?? throw new InvalidDataException($"Invalid session header: {path}");
        if (!string.Equals(header.Type, "session", StringComparison.Ordinal) || header.Version != 1)
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

        return new SessionDocument(path, header, turns);
    }

    public async Task<IReadOnlyList<SessionDocument>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_workspaceDirectory))
        {
            return [];
        }

        var documents = new List<SessionDocument>();
        foreach (var path in Directory.EnumerateFiles(_workspaceDirectory, "*.jsonl").OrderByDescending(path => File.GetLastWriteTimeUtc(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                documents.Add(await LoadAsync(path, cancellationToken));
            }
            catch (InvalidDataException)
            {
                // Ignore malformed files in listings. Explicit loads still surface the error.
            }
        }

        return documents;
    }

    public async Task<SessionDocument> ResolveAsync(string selector, CancellationToken cancellationToken = default)
    {
        if (File.Exists(selector))
        {
            return await LoadAsync(selector, cancellationToken);
        }

        var sessions = await ListAsync(cancellationToken);
        var matches = sessions
            .Where(session => session.Header.SessionId.StartsWith(selector, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => throw new FileNotFoundException($"No session matches '{selector}'."),
            1 => matches[0],
            _ => throw new InvalidOperationException($"Session id prefix '{selector}' is ambiguous."),
        };
    }

    public async Task<SessionDocument?> ContinueAsync(CancellationToken cancellationToken = default) =>
        (await ListAsync(cancellationToken)).FirstOrDefault();

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
