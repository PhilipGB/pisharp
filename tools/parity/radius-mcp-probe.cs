using System.Text.Json;
using PiSharp.Cli.Authentication;

using var input = JsonDocument.Parse(args[0]);
var results = new List<object>();
foreach (var scenario in input.RootElement.EnumerateArray())
{
    var root = Path.Combine(Path.GetTempPath(), "pisharp-radius-probe-" + Guid.NewGuid().ToString("N"));
    var agent = Path.Combine(root, "agent");
    Directory.CreateDirectory(agent);
    Directory.CreateDirectory(Path.Combine(root, ".pi"));
    try
    {
        var path = Path.Combine(agent, "mcp.json");
        if (scenario.TryGetProperty("global", out var global)) await File.WriteAllTextAsync(path, global.GetRawText());
        if (scenario.TryGetProperty("project", out var project))
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "mcp.json"), project.GetRawText());
        var setup = await RadiusMcpSetup.CreateAsync(agent, root);
        if (setup is not null && scenario.GetProperty("action").GetString() == "yes")
            await setup.SaveAsync();
        using var saved = JsonDocument.Parse(File.Exists(path) ? await File.ReadAllTextAsync(path) : "null");
        results.Add(new { id = scenario.GetProperty("id").GetString(), offered = setup is not null,
            global = saved.RootElement.Clone() });
    }
    finally { Directory.Delete(root, true); }
}
Console.WriteLine(JsonSerializer.Serialize(results));
