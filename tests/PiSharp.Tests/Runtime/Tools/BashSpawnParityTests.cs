using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class BashSpawnParityTests
{
    public static IEnumerable<object[]> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity", "fixtures",
            "builtin-coding-tool-bash-spawn-2026-10-22.json")));
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            yield return [row.GetProperty("caseId").GetString()!, row.GetRawText()];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualFunctionRejectsSpawnFailuresLikePinnedPi(string caseId, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var document = JsonDocument.Parse(fixture);
        var setup = document.RootElement.GetProperty("setup");
        var root = Path.Combine(Path.GetTempPath(), "pisharp-bash-spawn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in setup.GetProperty("files").EnumerateObject())
                await File.WriteAllTextAsync(Path.Combine(root, file.Name), file.Value.GetString());
            if (setup.TryGetProperty("directories", out var directories))
                foreach (var entry in directories.EnumerateObject())
                {
                    var path = Path.Combine(root, entry.Name);
                    Directory.CreateDirectory(path);
                    File.SetUnixFileMode(path, (UnixFileMode)entry.Value.GetInt32());
                }
            if (setup.TryGetProperty("modes", out var modes))
                foreach (var entry in modes.EnumerateObject())
                    File.SetUnixFileMode(Path.Combine(root, entry.Name), (UnixFileMode)entry.Value.GetInt32());
            var shell = setup.TryGetProperty("options", out var options) ? options.GetProperty("shellPath").GetString()!.Replace("{WORKSPACE}", root, StringComparison.Ordinal) : null;
            var cwd = setup.TryGetProperty("executionCwd", out var configuredCwd) ? configuredCwd.GetString()!.Replace("{WORKSPACE}", root, StringComparison.Ordinal) : root;
            var function = new CodingTools(cwd, shell).CreateAll().OfType<AIFunction>().Single(tool => tool.Name == "bash");
            var updates = new List<BashToolUpdate>();
            var arguments = new AIFunctionArguments(setup.GetProperty("arguments").EnumerateObject()
                .ToDictionary(item => item.Name, item => (object?)item.Value.Clone()))
            {
                Context = new Dictionary<object, object?> { [CodingTools.BashSnapshotContextKey] = (Action<BashToolUpdate>)updates.Add }
            };
            var failure = await Assert.ThrowsAsync<ToolFailureException>(() => function.InvokeAsync(arguments).AsTask());
            var expected = document.RootElement.GetProperty("expected");
            Assert.True(expected.GetProperty("rejected").GetBoolean(), caseId);
            Assert.Equal(expected.GetProperty("content")[0].GetProperty("text").GetString(), failure.Message.Replace(root, "<workspace>", StringComparison.Ordinal));
            var initial = Assert.Single(updates);
            Assert.Null(initial.Text);
            Assert.Null(initial.Details);
        }
        finally
        {
            foreach (var directory in Directory.GetDirectories(root)) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(root, recursive: true);
        }
    }
}
