using System.Text.Json;
using PiSharp.Runtime.Resources;

using var scenarios = JsonDocument.Parse(args[0]);
var results = new List<object>();
foreach (var scenario in scenarios.RootElement.EnumerateArray())
{
    var root = Path.Combine(Path.GetTempPath(), "pisharp-trust-null-probe-" + Guid.NewGuid().ToString("N"));
    var parent = Path.Combine(root, "parent");
    var project = Path.Combine(parent, "project");
    var agent = Path.Combine(root, "agent");
    var unrelated = Path.Combine(root, "unrelated");
    Directory.CreateDirectory(project);
    Directory.CreateDirectory(agent);
    try
    {
        var decisions = new Dictionary<string, bool?>
        {
            [project] = null,
            [unrelated] = null
        };
        if (scenario.TryGetProperty("parentDecision", out var parentDecision))
            decisions[parent] = parentDecision.ValueKind == JsonValueKind.Null ? null : parentDecision.GetBoolean();
        await File.WriteAllTextAsync(Path.Combine(agent, "trust.json"), JsonSerializer.Serialize(decisions));

        var trust = new ProjectTrust(agent);
        var before = await trust.GetEntryAsync(project);
        await trust.SetAsync(project, null);
        var after = await trust.GetEntryAsync(project);
        var saved = JsonSerializer.Deserialize<Dictionary<string, bool?>>(await File.ReadAllTextAsync(trust.PathOnDisk))!;
        var persisted = new SortedDictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var (path, decision) in saved)
            persisted[Path.GetRelativePath(root, path).Replace('\\', '/')] = decision;

        object? FormatEntry(ProjectTrustEntry? entry) => entry is null
            ? null
            : new { path = Path.GetRelativePath(root, entry.Path).Replace('\\', '/'), decision = entry.Decision };
        results.Add(new { id = scenario.GetProperty("id").GetString(), before = FormatEntry(before),
            after = FormatEntry(after), persisted });
    }
    finally { Directory.Delete(root, recursive: true); }
}
Console.WriteLine(JsonSerializer.Serialize(results));
