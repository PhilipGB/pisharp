using System.Text.Json;
using PiSharp.Runtime.Resources;

var cases = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var baseDirectory = args[1];
var results = new List<object>();
if (args.Length > 2 && args[2] == "file-url-errors")
{
    foreach (var item in cases.EnumerateArray())
    {
        var id = item.GetProperty("id").GetString()!;
        var pattern = item.GetProperty("pattern").GetString()!;
        results.Add(new
        {
            id,
            glob = Capture(() => LocalResourcePathRules.IsEnabledByOverrides(
                Path.Combine(baseDirectory, "plugin.dll"), ["!" + pattern], baseDirectory)),
            exact = Capture(() => LocalResourcePathRules.IsEnabledByOverrides(
                Path.Combine(baseDirectory, "plugin.dll"), ["+" + pattern], baseDirectory))
        });
    }

    Console.WriteLine(JsonSerializer.Serialize(results));
    return;
}

foreach (var item in cases.EnumerateArray())
{
    var id = item.GetProperty("id").GetString()!;
    var path = Path.GetFullPath(item.GetProperty("path").GetString()!, baseDirectory);
    var isSkill = item.GetProperty("resourceType").GetString() == "skills";
    bool actual;
    if (item.TryGetProperty("overrides", out var overrides))
    {
        actual = LocalResourcePathRules.IsEnabledByOverrides(path,
            overrides.EnumerateArray().Select(value => value.GetString()!.Replace("$BASE", baseDirectory,
                StringComparison.Ordinal)).ToArray(), baseDirectory, isSkill);
    }
    else
    {
    var pattern = item.GetProperty("pattern").GetString()!.Replace("$BASE", baseDirectory, StringComparison.Ordinal);
        actual = !LocalResourcePathRules.IsEnabledByOverrides(path, ["!" + pattern], baseDirectory, isSkill);
    }
    results.Add(new { id, actual });
}
Console.WriteLine(JsonSerializer.Serialize(results));

static object Capture(Func<bool> match)
{
    try { return new { threw = false, exceptionType = (string?)null, message = (string?)null, result = match() }; }
    catch (Exception exception)
    {
        return new { threw = true, exceptionType = exception.GetType().Name, message = exception.Message, result = (bool?)null };
    }
}
