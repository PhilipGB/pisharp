using System.Text;
using System.Text.Json;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class ProcessTextLinesTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var fixture = SearchProcessProtocolTests.SourceFixture("find");
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (row.GetProperty("expected").GetProperty("rejected").GetBoolean()) continue;
            foreach (var size in new[] { 1, 7, 8192 }) yield return [row.GetRawText(), size];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChunkedPathsPreservePinnedUnicodeDelimitersOrderAndTrimming(string fixture, int size)
    {
        using var document = JsonDocument.Parse(fixture);
        var row = document.RootElement;
        var paths = new List<string>();
        var reader = new ProcessTextLines(line =>
        {
            var path = ProcessTextLines.Trim(line).Replace("<workspace>/", "", StringComparison.Ordinal);
            if (path.Length > 0) paths.Add(path);
        });
        var bytes = Encoding.UTF8.GetBytes(row.GetProperty("stdout").GetString()!);
        for (var index = 0; index < bytes.Length; index += size)
            reader.Append(bytes.AsMemory(index, Math.Min(size, bytes.Length - index)));
        reader.Append(ReadOnlyMemory<byte>.Empty);
        reader.Append(ReadOnlyMemory<byte>.Empty);
        var expected = row.GetProperty("expected").GetProperty("content")[0].GetProperty("text").GetString()!.Split("\n\n[")[0];
        Assert.Equal(expected == "No files found matching pattern" ? "" : expected, string.Join('\n', paths));
    }
}
