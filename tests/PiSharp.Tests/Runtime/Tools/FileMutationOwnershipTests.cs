using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class FileMutationOwnershipTests
{
    public static IEnumerable<object[]> Cases()
    {
        var oracle = Environment.GetEnvironmentVariable("PISHARP_TEST_OWNERSHIP_ORACLE");
        using var source = oracle is null ? SearchProcessProtocolTests.SourceFixture("mutation", "ownership")
            : JsonDocument.Parse(File.ReadAllText(oracle));
        Assert.Equal("b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6", source.RootElement.GetProperty("pinnedPiSha").GetString());
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Assert.True(source.RootElement.GetProperty("alternateGroupAssigned").GetBoolean());
        foreach (var row in source.RootElement.GetProperty("cases").EnumerateArray())
            yield return [row.GetProperty("caseId").GetString()!, row.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RegisteredMutationPreservesOwnershipAndMatchesKernelPermissionChanges(string caseId, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var source = JsonDocument.Parse(fixture);
        var row = source.RootElement;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mutation-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var actor = (await ExecuteAsync("id", ["-u"], root)).Trim();
            var externalOracle = Environment.GetEnvironmentVariable("PISHARP_TEST_OWNERSHIP_ORACLE") is not null;
            if (!externalOracle && actor == "0") return;
            var path = Path.Combine(root, "file.txt");
            await File.WriteAllTextAsync(path, "alpha\n");
            var primary = (await ExecuteAsync("id", ["-g"], root)).Trim();
            foreach (var group in (await ExecuteAsync("id", ["-G"], root)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(group => group != primary))
            {
                var attempt = await DirectProcessExecution.ExecuteAsync("chgrp", [group, path], root, null, CancellationToken.None);
                if (attempt.ExitCode == 0) break;
            }
            File.SetUnixFileMode(path, (UnixFileMode)row.GetProperty("mode").GetInt32());
            var before = (await ExecuteAsync("stat", ["-c", "%u %g %a %i", path], root)).Trim().Split(' ');
            Assert.Equal(actor, before[0]);
            if (externalOracle)
            {
                Assert.Equal(row.GetProperty("before").GetProperty("uid").GetUInt32().ToString(CultureInfo.InvariantCulture), before[0]);
                Assert.Equal(row.GetProperty("before").GetProperty("gid").GetUInt32().ToString(CultureInfo.InvariantCulture), before[1]);
            }
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                Assert.NotEqual(primary, before[1]);
            var function = new CodingTools(root).CreateAll().OfType<AIFunction>()
                .Single(function => function.Name == row.GetProperty("tool").GetString());

            var value = await function.InvokeAsync(new AIFunctionArguments(row.GetProperty("arguments").EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone())));

            if (!ToolResultOutput.TryRead(value, out var text, out var details)) text = value?.ToString() ?? "";
            Assert.Equal(row.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString(), text);
            var expectedDetails = row.GetProperty("result").TryGetProperty("details", out var expected) ? expected : JsonSerializer.SerializeToElement<object?>(null);
            Assert.True(JsonElement.DeepEquals(expectedDetails, JsonSerializer.SerializeToElement(details, new JsonSerializerOptions(JsonSerializerDefaults.Web))), caseId);
            Assert.Equal(row.GetProperty("finalText").GetString(), await File.ReadAllTextAsync(path));
            var after = (await ExecuteAsync("stat", ["-c", "%u %g %a %i", path], root)).Trim().Split(' ');
            Assert.Equal(before[0], after[0]);
            Assert.Equal(before[1], after[1]);
            if (externalOracle)
            {
                Assert.Equal(row.GetProperty("after").GetProperty("uid").GetUInt32().ToString(CultureInfo.InvariantCulture), after[0]);
                Assert.Equal(row.GetProperty("after").GetProperty("gid").GetUInt32().ToString(CultureInfo.InvariantCulture), after[1]);
            }
            Assert.Equal(Convert.ToString(row.GetProperty("after").GetProperty("mode").GetInt32(), 8), after[2]);
            Assert.True(row.GetProperty("inodePreserved").GetBoolean());
            Assert.NotEqual(before[3], after[3]); // Atomic replacement remains an explicit inode-equivalence exclusion.
            Assert.Equal([path], Directory.GetFiles(root));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{caseId}: uid={after[0]}, gid={after[1]}, alternateGroup={after[1] != primary}, mode={after[2]}"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<string> ExecuteAsync(string command, IReadOnlyList<string> arguments, string root)
    {
        var result = await DirectProcessExecution.ExecuteAsync(command, arguments, root, null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        return result.Stdout;
    }
}
