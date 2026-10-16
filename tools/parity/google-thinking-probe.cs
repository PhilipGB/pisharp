using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Providers;

var cases = JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(args[0]))!;
var results = new List<object>();
foreach (var item in cases)
{
    var id = item.GetProperty("model").GetString()!;
    var level = item.GetProperty("level").GetString()!;
    var vertex = item.GetProperty("vertex").GetBoolean();
    var budgets = item.GetProperty("budgets").ValueKind == JsonValueKind.Object
        ? JsonSerializer.Deserialize<Dictionary<string, int>>(item.GetProperty("budgets")) : null;
    var model = new ModelDescriptor(id, "fixture", 128000, "configured", Reasoning: true);
    using var client = new GoogleGenAiChatClient(new HttpClient(), new Uri("https://example.invalid/v1beta"),
        "key", model, vertex ? new GoogleVertexRequestOptions(null, null, false) : null);
    var options = new ChatOptions { Reasoning = ThinkingLevels.ToOptions(level) };
    options.AdditionalProperties ??= new();
    options.AdditionalProperties[ThinkingBudgetPolicy.SettingsProperty] = budgets;
    var request = client.BuildRequest([new ChatMessage(ChatRole.User, "hello")], options);
    results.Add(new { model = id, level, vertex, budgets, thinking = request["generationConfig"]!["thinkingConfig"] });
}
Console.WriteLine(JsonSerializer.Serialize(results));
