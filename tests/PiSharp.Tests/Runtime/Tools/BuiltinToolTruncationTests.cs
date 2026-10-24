using System.Text.Json;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BuiltinToolTruncationTests
{
    public static IEnumerable<object[]> FixedNumberCases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity", "fixtures",
            "builtin-coding-tool-fixed-number-2026-10-22.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("value").GetDouble(), item.GetProperty("digits").GetInt32(), item.GetProperty("expected").GetString()!];
    }

    [Theory]
    [MemberData(nameof(FixedNumberCases))]
    public void CoordinateNumberFormattingMatchesBinaryValueRounding(double value, int digits, string expected) =>
        Assert.Equal(expected, JavaScriptNumberText.Fixed(value, digits));

    public static IEnumerable<object[]> CompleteCases()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var options = item.GetProperty("options");
            yield return [item.GetProperty("caseId").GetString()!, item.GetProperty("mode").GetString()!,
                item.GetProperty("content").GetString()!,
                options.TryGetProperty("maxLines", out var lines) ? lines.GetInt64() : 2000L,
                options.TryGetProperty("maxBytes", out var bytes) ? bytes.GetInt32() : 50 * 1024,
                item.GetProperty("expected").GetRawText()];
        }
    }

    [Theory]
    [MemberData(nameof(CompleteCases))]
    public void SharedHeadAndTailMatchTheCompletePinnedResult(string caseId, string mode, string input,
        long maxLines, int maxBytes, string expected)
    {
        using var document = JsonDocument.Parse(expected);
        var result = mode == "head" ? TextOutputTruncator.Head(input, maxLines, maxBytes) :
            TextOutputTruncator.Tail(input, maxLines, maxBytes);

        Assert.True(JsonElement.DeepEquals(document.RootElement,
            JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))), caseId);
    }

    public static IEnumerable<object[]> HeadCases()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var options = item.GetProperty("options");
            if (item.GetProperty("mode").GetString() != "head" ||
                !options.TryGetProperty("maxLines", out var maxLines) || maxLines.GetInt64() != 9_007_199_254_740_991L)
                continue;
            yield return [item.GetProperty("caseId").GetString()!, item.GetProperty("content").GetString()!,
                options.GetProperty("maxBytes").GetInt32(), item.GetProperty("expected").GetRawText()];
        }
    }

    [Theory]
    [MemberData(nameof(HeadCases))]
    public void ExistingHeadProjectionMatchesPinnedCountsAndCompleteMetadata(string caseId, string input,
        int maxBytes, string expected)
    {
        using var document = JsonDocument.Parse(expected);
        var (text, details) = ToolOutputTruncator.TruncateHead(input, maxBytes);

        Assert.Equal(document.RootElement.GetProperty("content").GetString(), text);
        if (document.RootElement.GetProperty("truncated").GetBoolean())
        {
            Assert.NotNull(details);
            Assert.True(JsonElement.DeepEquals(document.RootElement,
                JsonSerializer.SerializeToElement(details, new JsonSerializerOptions(JsonSerializerDefaults.Web))), caseId);
        }
        else Assert.Null(details);
    }

    private static JsonDocument Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory?.FullName ??
            throw new DirectoryNotFoundException("PiSharp.slnx not found."), "docs", "parity", "fixtures",
            "builtin-coding-tool-truncation-2026-10-22.json")));
    }
}
