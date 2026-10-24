using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BashToolUpdateParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity", "fixtures",
            "builtin-coding-tool-bash-updates-2026-10-22.json")));
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [row.GetProperty("caseId").GetString()!, row.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualFunctionMatchesPinnedInitialAndLastSnapshots(string caseId, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var document = JsonDocument.Parse(fixture);
        Assert.Equal(caseId, document.RootElement.GetProperty("caseId").GetString());
        var setup = document.RootElement.GetProperty("setup");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var updates = new List<JsonObject>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var options = setup.TryGetProperty("options", out var configured) ? configured : default;
            var shell = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("shellPath", out var shellPath)
                ? shellPath.GetString()!.Replace("{WORKSPACE}", root, StringComparison.Ordinal) : null;
            var prefix = options.ValueKind == JsonValueKind.Object && options.TryGetProperty("commandPrefix", out var commandPrefix)
                ? commandPrefix.GetString() : null;
            var function = new CodingTools(root, shell, shellCommandPrefix: prefix).CreateAll().OfType<AIFunction>().Single(tool => tool.Name == "bash");
            var arguments = new AIFunctionArguments(setup.GetProperty("arguments").EnumerateObject()
                .ToDictionary(item => item.Name, item => (object?)item.Value.Clone()))
            {
                Context = new Dictionary<object, object?>
                {
                    [CodingTools.BashSnapshotContextKey] = (Action<BashToolUpdate>)(update =>
                    {
                        var details = JsonSerializer.SerializeToNode(update.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        if (details is JsonObject obj && obj["fullOutputPath"] is JsonValue pathValue && pathValue.TryGetValue<string>(out var path))
                        {
                            lock (updates) paths.Add(path);
                            obj["fullOutputPath"] = "<full-output-1>";
                        }
                        var content = new JsonArray();
                        if (update.Text is not null) content.Add(new JsonObject { ["type"] = "text", ["text"] = update.Text });
                        lock (updates) updates.Add(new JsonObject { ["content"] = content, ["details"] = details });
                    })
                }
            };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (setup.TryGetProperty("preCancelled", out var preCancelled) && preCancelled.GetBoolean()) cancellation.Cancel();
            Exception? failure = null;
            try
            {
                var value = await function.InvokeAsync(arguments, cancellation.Token);
                if (ToolResultOutput.TryRead(value, out _, out var details))
                {
                    var json = JsonSerializer.SerializeToElement(details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("fullOutputPath", out var path)) paths.Add(path.GetString()!);
                }
            }
            catch (Exception error) { failure = error; }
            Assert.Equal(document.RootElement.GetProperty("expected").GetProperty("rejected").GetBoolean(), failure is not null);
            Assert.NotEmpty(updates);
            var expected = document.RootElement.GetProperty("expected").GetProperty("updateContract");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetProperty("initial").GetRawText()), updates[0]), caseId + " initial");
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetProperty("final").GetRawText()), updates[^1]), caseId + " final");
        }
        finally
        {
            foreach (var path in paths) File.Delete(path);
            Directory.Delete(root, recursive: true);
        }
    }
}
