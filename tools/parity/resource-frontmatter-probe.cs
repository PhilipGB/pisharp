using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Resources;

using var inputs = JsonDocument.Parse(File.ReadAllText(args[0]));
var results = new List<object>();
foreach (var scenario in inputs.RootElement.EnumerateArray())
{
    var id = scenario.GetProperty("id").GetString()!;
    var root = Path.Combine(args[1], id);
    var kind = scenario.GetProperty("kind").GetString()!;
    var files = scenario.GetProperty("paths").EnumerateArray().Select(x => Path.Combine(root, x.GetString()!)).ToArray();
    object? result = null;
    string? error = null;
    string[] warningMessages = [];
    try
    {
        var catalog = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false,
            additionalSkills: kind == "skill" ? files : [], additionalPrompts: kind == "prompt" ? files : []);
        warningMessages = catalog.Diagnostics.Select(d => d.Message).ToArray();
        var skills = new List<object>();
        foreach (var skill in catalog.Skills)
        {
            var expanded = await catalog.InvokeSkillAsync(skill.Name, "");
            var body = expanded[(expanded.IndexOf("\n\n", StringComparison.Ordinal) + 2)..^9];
            skills.Add(new { name = skill.Name, description = skill.Description, explicitOnly = skill.ExplicitOnly, path = Path.GetRelativePath(root, skill.Path), body = Summary(body) });
        }
        result = new { skills, prompts = catalog.Prompts.Select(p => new { name = p.Name, description = p.Description, argumentHint = p.ArgumentHint,
            path = Path.GetRelativePath(root, p.Path), body = Summary(p.Template) }).ToArray(),
            diagnostics = catalog.Diagnostics.Select(d => new { type = d.Type, path = Path.GetRelativePath(root, d.Path!),
                category = d.Message.StartsWith("name ", StringComparison.Ordinal) || d.Message.StartsWith("description ", StringComparison.Ordinal) ? d.Message : "parse warning" }).ToArray() };
    }
    catch (Exception caught) when (caught is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
    { error = caught.GetType().Name; }
    results.Add(new { id, result, error, warningMessages });
}
Console.WriteLine(JsonSerializer.Serialize(results));
static object Summary(string text) => new { hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), characters = text.Length };
