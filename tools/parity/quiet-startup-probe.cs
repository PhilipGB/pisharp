using System.Reflection;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

using var input = JsonDocument.Parse(args[0]);
var results = new List<object>();
foreach (var scenario in input.RootElement.EnumerateArray())
{
    var root = Path.Combine(Path.GetTempPath(), "pisharp-quiet-probe-" + Guid.NewGuid().ToString("N"));
    var agent = Path.Combine(root, "agent");
    Directory.CreateDirectory(agent);
    Directory.CreateDirectory(Path.Combine(root, ".pi"));
    try
    {
        var path = Path.Combine(agent, "settings.json");
        await File.WriteAllTextAsync(path, scenario.GetProperty("user").GetRawText());
        var settings = await UserSettings.LoadAsync(agent, _ => null);
        if (scenario.TryGetProperty("project", out var project))
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), project.GetRawText());
            if (scenario.GetProperty("trusted").GetBoolean())
                settings = settings.Overlay(await UserSettings.LoadProjectAsync(root));
        }
        var presentation = new TerminalStartupPresentation(settings.QuietStartup, scenario.GetProperty("verbose").GetBoolean());
        results.Add(new { id = scenario.GetProperty("id").GetString(), value = settings.QuietStartup?.ToSettingValue() ?? "false",
            header = presentation.ShowHeader, details = presentation.ShowDetails });
    }
    finally { Directory.Delete(root, true); }
}
var writes = new List<object>();
var writeRoot = Path.Combine(Path.GetTempPath(), "pisharp-quiet-save-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(writeRoot);
try
{
    var path = Path.Combine(writeRoot, "settings.json");
    await File.WriteAllTextAsync(path, "{\"hideThinkingBlock\":true}");
    foreach (var value in new[] { "header", "true", "false" })
    {
        await UserSettingsWriter.SetAsync(path, "quietStartup", value, userScope: true);
        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        writes.Add(state.RootElement.Clone());
    }
}
finally { Directory.Delete(writeRoot, true); }
var pickerType = typeof(TerminalSettingsPicker);
var definition = ((Array)pickerType.GetField("s_settings", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
    .Cast<object>().Single(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)! == "quietStartup");
var choices = (IEnumerable<(string Key, string? Value, string Label, string Description)>)pickerType.GetMethod("Values", BindingFlags.Instance | BindingFlags.NonPublic)!
    .Invoke(new TerminalSettingsPicker(new TerminalEditor()), [definition, "false"])!;
var setting = new { label = (string)definition.GetType().GetProperty("Label")!.GetValue(definition)!,
    description = (string)definition.GetType().GetProperty("Description")!.GetValue(definition)!,
    values = choices.Where(choice => choice.Value is not null).Select(choice => choice.Value).ToArray() };
Console.WriteLine(JsonSerializer.Serialize(new { results, writes, setting }));
