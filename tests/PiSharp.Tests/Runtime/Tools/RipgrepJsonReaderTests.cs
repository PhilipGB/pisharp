using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class RipgrepJsonReaderTests
{
    [Fact]
    public void HugeMatchingLineRetainsBoundedMemoryAndPinnedDisplayedPrefix()
    {
        using var source = JsonDocument.Parse(BuiltinGrepParityTests.Cases().Single(row => (string)row[0] == "grep-huge-single-line")[1].ToString()!);
        var expected = source.RootElement.GetProperty("expectedResult").GetProperty("content")[0].GetProperty("text").GetString()!.Split("\n\n")[0];
        var matches = new List<RipgrepMatch>();
        var reader = new RipgrepJsonReader(match => { matches.Add(match); return true; });
        var prefix = Encoding.UTF8.GetBytes("{\"type\":\"match\",\"data\":{\"path\":{\"text\":\"file.txt\"},\"lines\":{\"text\":\"needle");
        var chunk = Encoding.UTF8.GetBytes(new string('x', 8192));
        var suffix = Encoding.UTF8.GetBytes("\\n\"},\"line_number\":1,\"submatches\":[]}}\n");
        var before = GC.GetAllocatedBytesForCurrentThread();
        reader.Append(prefix);
        for (var index = 0; index < 12 * 1024 * 1024 / chunk.Length; index++) reader.Append(chunk);
        reader.Append(suffix);
        reader.Append(ReadOnlyMemory<byte>.Empty);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 * 1024, $"Reader allocated {allocated} bytes for a streamed 12 MiB line.");
        var observed = Assert.Single(matches);
        Assert.True(observed.LineTruncated);
        Assert.Equal(expected, "file.txt:1: " + observed.Text);
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var row in GrepProcessProtocolTests.Cases())
        {
            using var document = JsonDocument.Parse((string)row[1]);
            if (document.RootElement.GetProperty("expected").GetProperty("rejected").GetBoolean()) continue;
            foreach (var size in new[] { 1, 7, 8192 }) yield return [row[0], row[1], size];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChunkBoundariesRetainPinnedJsonValidationUnicodeAndDisplayedLines(string caseId, string fixture, int size)
    {
        using var document = JsonDocument.Parse(fixture);
        var row = document.RootElement;
        var stdout = row.TryGetProperty("stdout", out var supplied) ? supplied.GetString()! : BuildLargeEvent(row.GetProperty("stdoutRecipe"));
        var matches = new List<RipgrepMatch>();
        var reader = new RipgrepJsonReader(match => { matches.Add(match); return true; });
        var bytes = Encoding.UTF8.GetBytes(stdout);
        for (var start = 0; start < bytes.Length; start += size) reader.Append(bytes.AsMemory(start, Math.Min(size, bytes.Length - start)));
        reader.Append(ReadOnlyMemory<byte>.Empty);
        reader.Append(ReadOnlyMemory<byte>.Empty);
        var lines = matches.Where(match => match is { Path: not null, LineNumber: not null, Text: not null })
            .Select(match => match.Path!.Replace("<workspace>/", "", StringComparison.Ordinal) + ":" + JavaScriptNumberText.Format(match.LineNumber!.Value) + ": " + match.Text);
        var actual = matches.Count == 0 ? "No matches found" : string.Join('\n', lines);
        if (matches.Any(match => match.LineTruncated)) actual += "\n\n[Some lines truncated to 500 chars. Use read tool to see full lines]";
        Assert.Equal(row.GetProperty("expected").GetProperty("content")[0].GetProperty("text").GetString(), actual);
        Assert.Equal(caseId, row.GetProperty("caseId").GetString());
    }

    private static string BuildLargeEvent(JsonElement recipe)
    {
        var node = JsonNode.Parse(recipe.GetProperty("event").GetString()!)!;
        node["data"]!["submatches"] = new JsonArray(Enumerable.Range(0, recipe.GetProperty("submatches").GetInt32())
            .Select(index => (JsonNode?)new JsonObject { ["match"] = new JsonObject { ["text"] = "needle" }, ["start"] = index, ["end"] = index + 6 }).ToArray());
        return node.ToJsonString() + "\n";
    }
}
