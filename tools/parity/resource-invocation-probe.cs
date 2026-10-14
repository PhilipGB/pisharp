using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Resources;

using var inputs = JsonDocument.Parse(File.ReadAllText(args[0]));
var results = new List<object>();
foreach (var scenario in inputs.RootElement.EnumerateArray())
{
    var id = scenario.GetProperty("id").GetString()!;
    var root = Path.Combine(args[1], "pisharp", id);
    var file = Path.Combine(root, "guide", "SKILL.md");
    var catalog = await ResourceCatalog.LoadAsync(root, root, false, discoverSkills: false, discoverPrompts: false,
        additionalSkills: [file], additionalPrompts: [Path.Combine(root, "prompts")]);
    switch (scenario.GetProperty("after").GetString())
    {
        case "delete": File.Delete(file); break;
        case "invalid-yaml": File.WriteAllText(file, "---\ndescription: [broken\n---\nbody"); break;
        case "changed": File.WriteAllText(file, "---\ndescription: Updated\n---\nchanged body"); break;
        case "denied" when !OperatingSystem.IsWindows(): File.SetUnixFileMode(file, UnixFileMode.None); break;
    }
    string? text = null;
    string? exception = null;
    var errors = new List<object>();
    try
    {
        text = (await catalog.ResolveInputAsync(scenario.GetProperty("input").GetString()!, reportError: error =>
        {
            errors.Add(new { @event = error.Event, path = Path.GetRelativePath(root, error.ExtensionPath),
                message = error.Error.Replace(root, "<workspace>", StringComparison.Ordinal) });
            return Task.CompletedTask;
        })).Replace(root, "<workspace>", StringComparison.Ordinal);
    }
    catch (Exception error) { exception = error.GetType().Name; }
    results.Add(new { id, text, hash = text is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))), errors, exception });
}
Console.WriteLine(JsonSerializer.Serialize(results));
