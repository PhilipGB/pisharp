using System.Text.Json;
using PiSharp.Runtime.Resources;

var cases = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var baseDirectory = args[1];
var results = new List<object>();
foreach (var item in cases.EnumerateArray())
{
    var id = item.GetProperty("id").GetString()!;
    var path = Path.GetFullPath(item.GetProperty("path").GetString()!, baseDirectory);
    var isSkill = item.GetProperty("resourceType").GetString() == "skills";
    bool actual;
    if (item.TryGetProperty("overrides", out var overrides))
    {
        actual = LocalResourcePathRules.IsEnabledByOverrides(path,
            overrides.EnumerateArray().Select(value => value.GetString()!).ToArray(), baseDirectory, isSkill);
    }
    else
    {
    var pattern = item.GetProperty("pattern").GetString()!.Replace("$BASE", baseDirectory, StringComparison.Ordinal);
        actual = !LocalResourcePathRules.IsEnabledByOverrides(path, ["!" + pattern], baseDirectory, isSkill);
    }
    results.Add(new { id, actual });
}
Console.WriteLine(JsonSerializer.Serialize(results));
