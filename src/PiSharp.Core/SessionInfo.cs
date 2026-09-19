using System.Text.Json;

namespace PiSharp.Core;

/// <summary>
/// Lightweight metadata for one session file, sufficient for listing, searching, sorting,
/// and the picker without reconstructing a full runtime session (pinned session-manager.ts
/// <c>SessionInfo</c>). Built by <see cref="SessionInfoReader"/> from a bounded scan of the
/// JSONL file.
/// </summary>
public sealed record PiSessionInfo(
    string Path,
    string Id,
    string Cwd,
    string? Name,
    string? ParentSessionPath,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    int MessageCount,
    string FirstMessage,
    string AllMessagesText);

/// <summary>
/// Reads session metadata directly from JSONL files (pinned <c>buildSessionInfo</c>):
/// a streaming line scan that skips malformed lines, requires the first parsed entry to be
/// a session header, and never throws — discovery is best-effort so one corrupt file can
/// never hide the other sessions. The modified timestamp follows pinned semantics: the
/// latest user/assistant message timestamp, else the header timestamp, else the file mtime.
/// </summary>
public static class SessionInfoReader
{
    private const string NoMessages = "(no messages)";

    /// <summary>
    /// Reads the metadata for one session file. Returns null when the file is unreadable,
    /// its first parsed entry is not a session header, or any I/O or JSON error occurs.
    /// </summary>
    public static async Task<PiSessionInfo?> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = File.GetLastWriteTimeUtc(filePath);
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream);

            string? headerLine = null;
            var header = (Type: "", Id: "", Cwd: "", ParentSession: (string?)null,
                          Timestamp: (DateTimeOffset?)null, IsLegacyV1: false, SessionId: "");
            var messageCount = 0;
            var firstMessage = string.Empty;
            var allMessages = new List<string>();
            string? name = null;
            var lastActivityTime = 0L;
            var hasActivity = false;
            var sawLegacyTurn = false;
            var legacyMessageCount = 0;
            var legacyFirstMessage = string.Empty;
            var legacyLastTime = (DateTimeOffset?)null;

            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement entry;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    entry = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Pinned parseSessionEntryLine: malformed lines are skipped silently.
                    continue;
                }

                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("type", out var type) ||
                    type.ValueKind != JsonValueKind.String)
                {
                    if (headerLine is null)
                    {
                        return null;
                    }
                    continue;
                }

                var entryType = type.GetString()!;
                if (headerLine is null)
                {
                    // The first parsed entry must be a session header (Pi v3 or legacy
                    // PiSharp v1); anything else is not a session file at all.
                    if (entryType != "session")
                    {
                        return null;
                    }

                    if (entry.TryGetProperty("version", out var version) &&
                        version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1 &&
                        entry.TryGetProperty("sessionId", out var sessionId) &&
                        sessionId.ValueKind == JsonValueKind.String)
                    {
                        // Legacy PiSharp v1 turn-based session: readable compatibility source
                        // with its own property names (sessionId/workingDirectory/createdAtUtc).
                        header.IsLegacyV1 = true;
                        header.Id = sessionId.GetString()!;
                        header.SessionId = sessionId.GetString()!;
                        header.Cwd = GetString(entry, "workingDirectory") ?? string.Empty;
                        header.Timestamp = GetTimestamp(entry, "createdAtUtc");
                    }
                    else
                    {
                        if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                            string.IsNullOrEmpty(id.GetString()))
                        {
                            return null;
                        }

                        header.Id = id.GetString()!;
                        header.Cwd = GetString(entry, "cwd") ?? string.Empty;
                        header.ParentSession = GetString(entry, "parentSession");
                        header.Timestamp = GetTimestamp(entry, "timestamp");
                    }

                    headerLine = line;
                    continue;
                }

                if (header.IsLegacyV1)
                {
                    if (entryType == "turn" && entry.TryGetProperty("createdAtUtc", out var createdAt) &&
                        createdAt.ValueKind == JsonValueKind.String)
                    {
                        sawLegacyTurn = true;
                        legacyMessageCount += 2;
                        if (DateTimeOffset.TryParse(createdAt.GetString(), out var turnTime))
                        {
                            legacyLastTime = legacyLastTime is null || turnTime > legacyLastTime
                                ? turnTime
                                : legacyLastTime;
                        }

                        if (string.IsNullOrEmpty(legacyFirstMessage) &&
                            entry.TryGetProperty("userMessage", out var userMessage) &&
                            userMessage.ValueKind == JsonValueKind.String)
                        {
                            legacyFirstMessage = userMessage.GetString()!;
                        }
                    }
                    continue;
                }

                switch (entryType)
                {
                    case SessionEntryTypes.SessionInfo when entry.TryGetProperty("name", out var nameValue):
                        // Latest session_info wins; a blank name explicitly clears it.
                        name = nameValue.ValueKind == JsonValueKind.String
                            ? string.IsNullOrWhiteSpace(nameValue.GetString()) ? null : nameValue.GetString()!.Trim()
                            : name;
                        break;

                    case SessionEntryTypes.Message:
                        messageCount++;
                        if (!entry.TryGetProperty("message", out var message) ||
                            message.ValueKind != JsonValueKind.Object ||
                            !message.TryGetProperty("role", out var role) ||
                            role.ValueKind != JsonValueKind.String)
                        {
                            break;
                        }

                        var roleText = role.GetString()!;
                        if (roleText is not ("user" or "assistant"))
                        {
                            break;
                        }

                        var activityTime = GetMessageActivityTime(message, entry);
                        if (activityTime is long millis)
                        {
                            hasActivity = true;
                            lastActivityTime = Math.Max(lastActivityTime, millis);
                        }

                        var text = ExtractTextContent(message);
                        if (text.Length == 0)
                        {
                            break;
                        }

                        allMessages.Add(text);
                        if (roleText == "user" && firstMessage.Length == 0)
                        {
                            firstMessage = text;
                        }
                        break;
                }
            }

            if (headerLine is null)
            {
                return null;
            }

            var cwd = header.Cwd;
            var created = header.Timestamp ?? stats;
            DateTimeOffset modified;
            if (header.IsLegacyV1)
            {
                modified = legacyLastTime ?? created;
            }
            else if (hasActivity)
            {
                modified = DateTimeOffset.FromUnixTimeMilliseconds(lastActivityTime);
            }
            else if (header.Timestamp is { } headerTime)
            {
                modified = headerTime;
            }
            else
            {
                modified = stats;
            }

            return header.IsLegacyV1
                ? new PiSessionInfo(
                    filePath,
                    header.SessionId,
                    cwd,
                    null,
                    null,
                    created,
                    modified,
                    sawLegacyTurn ? legacyMessageCount : 0,
                    string.IsNullOrEmpty(legacyFirstMessage) ? NoMessages : legacyFirstMessage,
                    string.IsNullOrEmpty(legacyFirstMessage) ? string.Empty : legacyFirstMessage)
                : new PiSessionInfo(
                    filePath,
                    header.Id,
                    cwd,
                    name,
                    header.ParentSession,
                    created,
                    modified,
                    messageCount,
                    firstMessage.Length == 0 ? NoMessages : firstMessage,
                    string.Join(" ", allMessages));
        }
        catch (Exception)
        {
            // Discovery is best-effort: unreadable or oversized files are not sessions.
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? GetTimestamp(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(value.GetString(), out var timestamp)
            ? timestamp
            : null;
    }

    /// <summary>Pinned getMessageActivityTime: the message's epoch-millis timestamp, else the entry timestamp.</summary>
    private static long? GetMessageActivityTime(JsonElement message, JsonElement entry)
    {
        if (message.TryGetProperty("timestamp", out var millis) &&
            millis.ValueKind == JsonValueKind.Number &&
            millis.TryGetInt64(out var value))
        {
            return value;
        }

        return GetTimestamp(entry, "timestamp") is { } timestamp
            ? timestamp.ToUnixTimeMilliseconds()
            : null;
    }

    /// <summary>Pinned extractTextContent: string content, or the text blocks joined by spaces.</summary>
    private static string ExtractTextContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            content.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object &&
                               item.TryGetProperty("type", out var itemType) &&
                               string.Equals(itemType.GetString(), "text", StringComparison.Ordinal) &&
                               item.TryGetProperty("text", out var text))
                .Select(item => item.GetProperty("text").GetString() ?? string.Empty));
    }
}
