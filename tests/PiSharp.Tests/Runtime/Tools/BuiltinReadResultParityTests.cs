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
        foreach (var name in new[] { "builtin-coding-tool-read-results-2026-10-22.json", "builtin-coding-tool-read-text-2026-10-22.json" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity", "fixtures", name)));
            foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
                yield return [item.GetProperty("caseId").GetString()!, item.GetRawText()];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualFunctionRetainsPinnedTextDetailsAndStructuredOutput(string caseId, string fixture)
    {
        using var document = JsonDocument.Parse(fixture);
        if (document.RootElement.TryGetProperty("linuxOnly", out var linux) && linux.GetBoolean() && !OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-read-result-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var modePaths = new List<string>();
        try
        {
            var setup = document.RootElement.GetProperty("setup");
            if (setup.TryGetProperty("directories", out var directories))
                foreach (var item in directories.EnumerateObject()) Directory.CreateDirectory(Path.Combine(root, item.Name));
            foreach (var file in setup.GetProperty("files").EnumerateObject())
            {
                var path = Path.Combine(root, file.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var value = file.Value;
                await File.WriteAllBytesAsync(path, Bytes(value));
            }
            if (setup.TryGetProperty("symlinks", out var links))
                foreach (var link in links.EnumerateObject()) File.CreateSymbolicLink(Path.Combine(root, link.Name), link.Value.GetString()!);
            if (OperatingSystem.IsLinux() && setup.TryGetProperty("modes", out var modes))
                foreach (var item in modes.EnumerateObject())
                {
                    var path = Path.Combine(root, item.Name);
                    modePaths.Add(path);
                    File.SetUnixFileMode(path, (UnixFileMode)item.Value.GetInt32());
                }
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(item => item.Name == "read");
            var arguments = new AIFunctionArguments(setup.GetProperty("arguments").EnumerateObject()
                .ToDictionary(item => item.Name, item => item.Value.ValueKind == JsonValueKind.String
                    ? (object?)item.Value.GetString()!.Replace("{WORKSPACE}", root, StringComparison.Ordinal) : item.Value.Clone()));
            using var cancellation = new CancellationTokenSource();
            if (setup.TryGetProperty("preCancelled", out var cancelled) && cancelled.GetBoolean()) cancellation.Cancel();
            var expected = document.RootElement.GetProperty("expected");
            if (expected.GetProperty("rejected").GetBoolean())
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(async () => await function.InvokeAsync(arguments, cancellation.Token));
                Assert.Equal(expected.GetProperty("content")[0].GetProperty("text").GetString(),
                    error.Message.Replace(root, "<workspace>", StringComparison.Ordinal));
                return;
            }

            var valueResult = await function.InvokeAsync(arguments, cancellation.Token);

            Assert.True(ToolResultOutput.TryReadContract(valueResult, out var result), caseId);
            Assert.Equal(expected.GetProperty("content")[0].GetProperty("text").GetString(), result.Text);
            Assert.Equal(expected.GetProperty("isError").GetBoolean(), result.IsError);
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("details"), JsonSerializer.SerializeToElement(
                result.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web))), caseId + " details");
            Assert.NotNull(result.StructuredContent);
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("structuredContent"), result.StructuredContent.Value),
                caseId + " structuredContent");
        }
        finally
        {
            if (OperatingSystem.IsLinux())
                foreach (var path in modePaths) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Bytes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return Encoding.UTF8.GetBytes(value.GetString()!);
        if (value.TryGetProperty("base64", out var encoded)) return Convert.FromBase64String(encoded.GetString()!);
        if (value.TryGetProperty("concat", out var parts)) return parts.EnumerateArray().SelectMany(Bytes).ToArray();
        var unit = value.TryGetProperty("repeat", out var text) ? Encoding.UTF8.GetBytes(text.GetString()!) :
            Convert.FromBase64String(value.GetProperty("base64Repeat").GetString()!);
        var count = value.GetProperty("count").GetInt32();
        var output = new byte[checked(unit.Length * count)];
        for (var index = 0; index < count; index++) unit.CopyTo(output, index * unit.Length);
        return output;
    }
}
