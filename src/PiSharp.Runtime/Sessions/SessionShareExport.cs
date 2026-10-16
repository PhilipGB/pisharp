using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Runtime.Sessions;

/// <summary>Projects Pi's share branch and HTML viewer data without mutating the saved session.</summary>
public static class SessionShareExport
{
    public static string ExportBranch(ConversationSession session, JsonElement presentation)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = session.Snapshot();
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var records = PiJsonlSessionInterchange.ProjectEntries(snapshot)
            .ToDictionary(entry => entry.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var output = new StringBuilder();
        output.Append(JsonSerializer.Serialize(new
        {
            type = "session",
            version = 3,
            id = snapshot.Id,
            timestamp,
            cwd = snapshot.WorkingDirectory
        })).Append('\n');
        string? parentId = null;
        foreach (var node in snapshot.Tree.ActivePath())
        {
            var entry = JsonNode.Parse(records[node.Id].GetRawText())!.AsObject();
            entry["parentId"] = parentId;
            output.Append(entry.ToJsonString()).Append('\n');
            parentId = node.Id;
        }
        output.Append(JsonSerializer.Serialize(new
        {
            type = "custom",
            customType = "pi.share",
            id = Guid.NewGuid().ToString("N")[..8],
            parentId,
            timestamp,
            data = presentation
        })).Append('\n');
        return output.ToString();
    }

    internal static string ViewerDataBase64(ConversationSession session, JsonElement presentation)
    {
        var snapshot = session.Snapshot();
        var header = snapshot.PiJsonlHeader ?? JsonSerializer.SerializeToElement(new
        {
            type = "session",
            version = 3,
            id = snapshot.Id,
            timestamp = (snapshot.Tree.Entries.FirstOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            cwd = snapshot.WorkingDirectory
        });
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            header,
            entries = PiJsonlSessionInterchange.ProjectEntries(snapshot),
            leafId = snapshot.Tree.HeadId,
            systemPrompt = presentation.GetProperty("systemPrompt"),
            tools = presentation.GetProperty("tools")
        });
        return Convert.ToBase64String(data);
    }
}
