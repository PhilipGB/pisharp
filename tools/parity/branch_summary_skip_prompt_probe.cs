using System.Text.Json;
using PiSharp.Cli;

// PiSharp half of the branchSummary.skipPrompt differential. The pinned-Pi half is
// branch_summary_skip_prompt_oracle.mjs; both consume branch_summary_skip_prompt_cases.json.
var root = Path.Combine(Path.GetTempPath(), "pisharp-skip-prompt-probe-" + Guid.NewGuid().ToString("N"));
var agent = Path.Combine(root, "agent");
var project = Path.Combine(root, "project");
Directory.CreateDirectory(agent);
Directory.CreateDirectory(Path.Combine(project, ".pi"));
var results = new List<object>();
try
{
    using var cases = JsonDocument.Parse(args[0]);
    foreach (var item in cases.RootElement.GetProperty("cases").EnumerateArray())
    {
        var id = item.GetProperty("id").GetString();
        foreach (var path in Directory.GetFiles(agent)) File.Delete(path);
        foreach (var path in Directory.GetFiles(Path.Combine(project, ".pi"))) File.Delete(path);
        object? error = null;
        object? skipPrompt = null;
        object? parent = null;
        int? reserve = null;
        bool? prompts = null;
        try
        {
            // Duplicate JSON keys only survive as raw text, so a case may supply userRaw/projectRaw.
            static string? Raw(JsonElement item, string scope) =>
                item.TryGetProperty(scope + "Raw", out var raw) && raw.ValueKind == JsonValueKind.String
                    ? raw.GetString()
                    : item.TryGetProperty(scope, out var value) && value.ValueKind != JsonValueKind.Null
                        ? value.GetRawText()
                        : null;
            await File.WriteAllTextAsync(Path.Combine(agent, "settings.json"),
                Raw(item, "user") ?? "{}");
            var loaded = await UserSettings.LoadAsync(agent, _ => null);
            var projectSettings = new UserSettings();
            if (item.GetProperty("projectTrusted").GetBoolean() && Raw(item, "project") is { } projectJson)
            {
                await File.WriteAllTextAsync(Path.Combine(project, ".pi", "settings.json"), projectJson);
                projectSettings = await UserSettings.LoadProjectAsync(project);
            }
            var effective = loaded.Overlay(projectSettings);
            var effectiveBranch = effective.EffectiveBranchSummary;
            skipPrompt = effectiveBranch.SkipPrompt is { } stored
                ? new { kind = stored.Kind.ToString(), raw = stored.Raw }
                : null;
            parent = effective.BranchSummary is { } value
                ? new { present = true, isObject = value.Settings is not null }
                : new { present = false, isObject = false };
            prompts = !effectiveBranch.SkipsSummaryPrompt;
            reserve = effectiveBranch.EffectiveReserveTokens;
        }
        catch (Exception exception)
        {
            error = exception.GetType().Name + ": " + exception.Message;
        }
        results.Add(new { id, loaded = error is null, error, skipPrompt, parent, prompts, reserve });
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}
Console.WriteLine(JsonSerializer.Serialize(results));
