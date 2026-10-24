using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinBashResultParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity",
            "fixtures", "builtin-coding-tool-bash-results-2026-10-22.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("caseId").GetString()!, item.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualBashFunctionRetainsPinnedMetadataAndPrivateCompleteOutput(string caseId, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? fullOutputPath = null;
        try
        {
            using var document = JsonDocument.Parse(fixture);
            var expected = document.RootElement;
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(item => item.Name == "bash");
            var arguments = new AIFunctionArguments(expected.GetProperty("arguments").EnumerateObject()
                .ToDictionary(item => item.Name, item => (object?)item.Value.Clone()));

            var value = await function.InvokeAsync(arguments);

            Assert.True(ToolResultOutput.TryReadContract(value, out var result), caseId);
            var structured = Assert.IsType<JsonElement>(result.StructuredContent);
            Assert.Equal(expected.GetProperty("isError").GetBoolean(), result.IsError);
            var expectedStructured = expected.GetProperty("structured");
            Assert.Equal(expectedStructured.GetProperty("exitCode").GetInt32(), structured.GetProperty("exit_code").GetInt32());
            Assert.Equal(expectedStructured.GetProperty("truncated").GetBoolean(), structured.GetProperty("truncated").GetBoolean());
            var duration = structured.GetProperty("wall_time_seconds").GetDouble();
            Assert.True(double.IsFinite(duration) && duration >= 0);
            var output = Encoding.UTF8.GetBytes(structured.GetProperty("output").GetString()!);
            Assert.Equal(expectedStructured.GetProperty("outputUtf8Bytes").GetInt32(), output.Length);
            Assert.Equal(expectedStructured.GetProperty("outputSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(output)));

            var details = JsonSerializer.SerializeToElement(result.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (details.ValueKind == JsonValueKind.Object && details.TryGetProperty("fullOutputPath", out var path))
                fullOutputPath = path.GetString();
            var text = fullOutputPath is null ? result.Text : result.Text.Replace(fullOutputPath, "<full-output-1>", StringComparison.Ordinal);
            Assert.Equal(expected.GetProperty("text").GetString(), text);
            var normalizedDetails = JsonNode.Parse(details.GetRawText());
            if (fullOutputPath is not null) normalizedDetails!["fullOutputPath"] = "<full-output-1>";
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("details"), JsonSerializer.SerializeToElement(normalizedDetails)), caseId);

            if (expected.GetProperty("artifacts").GetArrayLength() > 0)
            {
                Assert.NotNull(fullOutputPath);
                Assert.StartsWith(Path.GetTempPath(), fullOutputPath, StringComparison.Ordinal);
                Assert.Matches("^pi-bash-[a-f0-9]+\\.log$", Path.GetFileName(fullOutputPath));
                var artifact = expected.GetProperty("artifacts")[0];
                Assert.True(File.Exists(fullOutputPath));
                var bytes = await File.ReadAllBytesAsync(fullOutputPath);
                Assert.Equal(artifact.GetProperty("size").GetInt64(), bytes.LongLength);
                Assert.Equal(artifact.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
                Assert.Equal(artifact.GetProperty("mode").GetInt32(), (int)File.GetUnixFileMode(fullOutputPath));
            }
            else Assert.Null(fullOutputPath);
        }
        finally
        {
            if (fullOutputPath is not null) File.Delete(fullOutputPath);
            Directory.Delete(root, recursive: true);
        }
    }
}
