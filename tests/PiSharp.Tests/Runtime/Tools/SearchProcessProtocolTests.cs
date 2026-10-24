using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

[Collection("Extension environment")]
public sealed class SearchProcessProtocolTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var tool in new[] { "grep", "find" })
        {
            using var fixture = SourceFixture(tool);
            if (tool == "find") Assert.Equal(".git", fixture.RootElement.GetProperty("repositoryMarker").GetString());
            foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                var input = JsonNode.Parse(row.GetRawText())!;
                input["tool"] = tool;
                yield return [row.GetProperty("caseId").GetString()!, input.ToJsonString()];
            }
        }
    }

    internal static JsonDocument SourceFixture(string tool = "grep", string kind = "protocol")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) directory = directory.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "docs", "parity", "fixtures",
            $"builtin-coding-tool-{tool}-{kind}-2026-10-22.json")));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RegisteredFunctionMatchesPinnedSyntheticProcessBoundary(string caseId, string fixture)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var document = JsonDocument.Parse(fixture);
        var row = document.RootElement;
        Assert.Equal(caseId, row.GetProperty("caseId").GetString());
        var root = Path.Combine(Path.GetTempPath(), "pisharp-grep-protocol-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        Directory.CreateDirectory(bin);
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace, "file.txt"), "needle\n");
            var stdout = row.TryGetProperty("stdout", out var supplied) ? supplied.GetString()! : BuildLargeEvent(row.GetProperty("stdoutRecipe"));
            var stdoutFile = Path.Combine(root, "stdout");
            var stderrFile = Path.Combine(root, "stderr");
            var toolName = row.GetProperty("tool").GetString()!;
            var executable = Path.Combine(bin, toolName == "grep" ? "rg" : "fd");
            await File.WriteAllTextAsync(stdoutFile, stdout.Replace("<workspace>", workspace, StringComparison.Ordinal));
            await File.WriteAllTextAsync(stderrFile, row.GetProperty("stderr").GetString());
            await File.WriteAllTextAsync(executable, row.GetProperty("header").GetString() +
                "if [ \"$1\" = \"--version\" ]; then printf 'synthetic-rg-protocol-fixture\\n'; exit 0; fi\n" +
                $"cat {ProcessTestHelpers.ShellQuote(stdoutFile)}\ncat {ProcessTestHelpers.ShellQuote(stderrFile)} >&2\nexit {row.GetProperty("exitCode").GetInt32()}\n");
            File.SetUnixFileMode(executable, (UnixFileMode)row.GetProperty("mode").GetInt32());
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + inheritedPath);
            var function = new CodingTools(workspace).CreateAll().OfType<AIFunction>().Single(tool => tool.Name == toolName);
            var arguments = new AIFunctionArguments(row.GetProperty("arguments").EnumerateObject().ToDictionary<JsonProperty, string, object?>(item => item.Name,
                item => item.Value.ValueKind == JsonValueKind.String ? (object?)item.Value.GetString()! :
                    item.Value.ValueKind == JsonValueKind.Object ? string.Concat(Enumerable.Repeat(item.Value.GetProperty("repeat").GetString(), item.Value.GetProperty("count").GetInt32())) : item.Value.Clone()));
            var expected = row.GetProperty("expected");
            string text;
            object? details = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                var value = await function.InvokeAsync(arguments, timeout.Token);
                Assert.False(expected.GetProperty("rejected").GetBoolean());
                Assert.True(ToolResultOutput.TryRead(value, out text, out details));
            }
            catch (Exception error) when (error is not Xunit.Sdk.XunitException)
            {
                Assert.True(expected.GetProperty("rejected").GetBoolean(), error.ToString());
                text = error.Message;
            }
            Assert.Equal(expected.GetProperty("content")[0].GetProperty("text").GetString(), text.Replace(executable, "<executable>", StringComparison.Ordinal).Replace(workspace, "<workspace>", StringComparison.Ordinal));
            var actualDetails = JsonSerializer.SerializeToElement(details, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("details"), actualDetails), caseId + " details");
            Assert.Equal("needle\n", await File.ReadAllTextAsync(Path.Combine(workspace, "file.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", inheritedPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicAndRegisteredGrepPreserveRepositoryArgumentSemantics(bool publicFacade)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "pisharp-grep-public-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            var executable = Path.Combine(root, "rg");
            var argumentsPath = Path.Combine(root, "arguments");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\n' \"$@\" > " + ProcessTestHelpers.ShellQuote(argumentsPath) + "\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + inheritedPath);
            if (publicFacade)
                Assert.Equal("No matches found", await new SearchTools(root).Grep("needle"));
            else
            {
                var tool = new CodingTools(root).CreateAll().OfType<AIFunction>().Single(tool => tool.Name == "grep");
                var value = await tool.InvokeAsync(new AIFunctionArguments { ["pattern"] = "needle" });
                Assert.True(ToolResultOutput.TryRead(value, out var text, out _));
                Assert.Equal("No matches found", text);
            }
            var arguments = await File.ReadAllLinesAsync(argumentsPath);
            Assert.DoesNotContain("--no-require-git", arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", inheritedPath);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string BuildLargeEvent(JsonElement recipe)
    {
        var node = JsonNode.Parse(recipe.GetProperty("event").GetString()!)!;
        node["data"]!["submatches"] = new JsonArray(Enumerable.Range(0, recipe.GetProperty("submatches").GetInt32())
            .Select(index => (JsonNode?)new JsonObject { ["match"] = new JsonObject { ["text"] = "needle" }, ["start"] = index, ["end"] = index + 6 }).ToArray());
        return node.ToJsonString() + "\n";
    }
}
