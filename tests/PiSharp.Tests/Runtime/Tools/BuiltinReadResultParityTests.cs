using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinReadResultParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity",
            "fixtures", "builtin-coding-tool-read-results-2026-10-22.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("caseId").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualFunctionRetainsPinnedTextDetailsAndStructuredOutput(string caseId, string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-read-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var document = JsonDocument.Parse(fixture);
            var setup = document.RootElement.GetProperty("setup");
            foreach (var file in setup.GetProperty("files").EnumerateObject())
            {
                var path = Path.Combine(root, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var value = file.Value;
                if (value.ValueKind == JsonValueKind.String)
                    await File.WriteAllTextAsync(path, value.GetString());
                else
                {
                    var unit = value.TryGetProperty("repeat", out var text)
                        ? Encoding.UTF8.GetBytes(text.GetString()!)
                        : Convert.FromBase64String(value.GetProperty("base64Repeat").GetString()!);
                    await using var stream = File.Create(path);
                    for (var index = 0; index < value.GetProperty("count").GetInt32(); index++)
                        await stream.WriteAsync(unit);
                }
            }
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(item => item.Name == "read");
            var arguments = new AIFunctionArguments(setup.GetProperty("arguments").EnumerateObject()
                .ToDictionary(item => item.Name, item => (object?)item.Value.Clone()));

            var valueResult = await function.InvokeAsync(arguments);

            Assert.True(ToolResultOutput.TryReadContract(valueResult, out var result), caseId);
            var expected = document.RootElement.GetProperty("expected");
            Assert.Equal(expected.GetProperty("content")[0].GetProperty("text").GetString(), result.Text);
            Assert.Equal(expected.GetProperty("isError").GetBoolean(), result.IsError);
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("details"), JsonSerializer.SerializeToElement(
                result.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web))), caseId + " details");
            Assert.NotNull(result.StructuredContent);
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("structuredContent"), result.StructuredContent.Value),
                caseId + " structuredContent");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
