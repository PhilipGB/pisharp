using System.Text.Json;
using PiSharp.Runtime.Providers;

var cases = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(args[0]))!;
var results = new List<object>();
foreach (var item in cases)
{
    var baseMaxTokens = item.TryGetProperty("baseMaxTokens", out var baseValue) && baseValue.ValueKind == JsonValueKind.Number
        ? baseValue.GetInt32() : (int?)null;
    var modelMaxTokens = item.GetProperty("modelMaxTokens").GetInt32();
    var level = item.GetProperty("level").GetString()!;
    var budgets = JsonSerializer.Deserialize<Dictionary<string, int>>(item.GetProperty("budgets"));
    var adjusted = ThinkingBudgetPolicy.AdjustMaxTokens(baseMaxTokens, modelMaxTokens, level, budgets);
    results.Add(new
    {
        id = item.GetProperty("id").GetString(),
        maxTokens = adjusted.MaxTokens,
        thinkingBudget = Math.Min(adjusted.ThinkingBudget,
            Math.Max(0, adjusted.MaxTokens - ThinkingBudgetPolicy.MinimumAnswerTokens))
    });
}
Console.WriteLine(JsonSerializer.Serialize(results));
