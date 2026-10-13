using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core;
using static PiSharp.Runtime.Sessions.PiJsonlEntryCodec;

namespace PiSharp.Runtime.Sessions;

/// <summary>Orders complete Pi trees and writes private, non-overwriting exports.</summary>
internal static class PiJsonlSessionWriter
{
    /// <summary>Serializes all branches as current Pi v3 JSONL; unknown imported records are preserved verbatim.</summary>
    public static string Export(ConversationSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var output = new StringBuilder();
        var header = session.PiJsonlHeader is { } sourceHeader && sourceHeader.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(sourceHeader.GetRawText())!.AsObject()
            : new JsonObject();
        header["type"] = "session";
        header["version"] = 3;
        header["id"] = session.Id;
        header["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        header["cwd"] = session.WorkingDirectory;
        if (session.ParentSessionPath is not null) header["parentSession"] = session.ParentSessionPath;
        AppendLine(output, header);

        var orderedEntries = OrderedEntriesForExport(session);
        foreach (var node in orderedEntries)
            AppendLine(output, ProjectEntry(session, node));

        var activePath = session.Tree.ActivePath();
        var lastInfo = activePath.LastOrDefault(node => node.Type == "session_info");
        var representedName = lastInfo is null ? null : OriginalEntry(lastInfo) is { } nameEntry
            ? StringProperty(nameEntry, "name") : StringProperty(lastInfo.Payload, "name");
        if (string.IsNullOrWhiteSpace(representedName)) representedName = null;
        var needsHeadAnchor = orderedEntries.LastOrDefault()?.Id != session.Tree.HeadId;
        if (needsHeadAnchor || session.Name != representedName)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var info = new JsonObject
            {
                ["type"] = "session_info",
                ["id"] = id,
                ["parentId"] = session.Tree.HeadId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            };
            if (session.Name is not null) info["name"] = session.Name;
            AppendLine(output, info);
        }
        return output.ToString();
    }

    /// <summary>Writes Pi JSONL without replacing an existing export; files are user-private on Unix.</summary>
    public static async Task ExportToFileAsync(ConversationSession session, string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(directory);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var bytes = Encoding.UTF8.GetBytes(Export(session));
        var created = false;
        try
        {
            await using var file = new FileStream(target, options);
            created = true;
            await file.WriteAsync(bytes, cancellationToken);
            file.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(target); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static IReadOnlyList<ConversationNode> OrderedEntriesForExport(ConversationSession session)
    {
        var entries = session.Tree.Entries;
        var activeIds = session.Tree.ActivePath().Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<ConversationNode>(entries.Count);
        while (ordered.Count < entries.Count)
        {
            var next = entries.FirstOrDefault(node => !emitted.Contains(node.Id) &&
                (node.ParentId is null || emitted.Contains(node.ParentId)) && !activeIds.Contains(node.Id));
            next ??= entries.FirstOrDefault(node => !emitted.Contains(node.Id) &&
                (node.ParentId is null || emitted.Contains(node.ParentId)));
            if (next is null)
                throw new InvalidDataException("Session tree cannot be written in parent-before-child order.");
            ordered.Add(next);
            emitted.Add(next.Id);
        }
        return ordered;
    }

    private static void AppendLine(StringBuilder output, JsonObject record) => output.Append(record.ToJsonString()).Append('\n');

}
