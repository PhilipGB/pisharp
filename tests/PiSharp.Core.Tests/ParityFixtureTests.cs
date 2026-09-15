using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class ParityFixtureTests
{
    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi-v3");

    [Fact]
    public void SessionFixtureUsesPiV3TypedEntryShapes()
    {
        var lines = ReadJsonLines("pi-session.jsonl");

        Assert.Equal("session", lines[0].GetProperty("type").GetString());
        Assert.Equal(3, lines[0].GetProperty("version").GetInt32());
        Assert.Contains(lines.Skip(1), entry => entry.GetProperty("type").GetString() == "message");
        Assert.Contains(lines, entry => entry.GetProperty("type").GetString() == "model_change");
        Assert.Contains(lines, entry => entry.GetProperty("type").GetString() == "label");
    }

    [Fact]
    public void ProtocolFixturesContainCurrentEventAndRpcFamilies()
    {
        var events = ReadJsonLines("json-events.jsonl");
        var requests = ReadJsonLines("rpc-requests.jsonl");

        Assert.Contains(events, entry => entry.GetProperty("type").GetString() == "message_update");
        Assert.Contains(events, entry => entry.GetProperty("type").GetString() == "auto_retry_start");
        Assert.Contains(events, entry => entry.GetProperty("type").GetString() == "compaction_end");
        Assert.Contains(requests, entry => entry.GetProperty("type").GetString() == "get_available_models");
        Assert.Contains(requests, entry => entry.GetProperty("type").GetString() == "set_session_name");
    }

    [Fact]
    public void ResourceFixturesMatchPiSharpCatalogInputs()
    {
        var skill = new SkillCatalog().Discover(
            FixtureRoot,
            additionalPaths: [Path.Combine(FixtureRoot, "skill")],
            includeDefaults: false);
        var prompts = new PromptTemplateCatalog().Discover(
            FixtureRoot,
            explicitPaths: [Path.Combine(FixtureRoot, "prompt-template.md")],
            includeDefaults: false);
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRoot, "package.json")));

        Assert.Contains(skill.Skills, item => item.Name == "review-code");
        Assert.Contains(prompts, item => item.Name == "prompt-template");
        Assert.True(package.RootElement.GetProperty("pi").GetProperty("extensions").GetArrayLength() > 0);
    }

    [Fact]
    public void ToolFixtureAnchorsPiDefaultAndOptionalToolSets()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRoot, "tool-schemas.json")));
        var root = document.RootElement;
        var defaults = root.GetProperty("defaultTools").EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();
        var allTools = root.GetProperty("allTools").EnumerateArray()
            .Select(value => value.GetString() ?? string.Empty)
            .ToArray();

        Assert.Equal(["read", "bash", "edit", "write"], defaults);
        Assert.Contains("powershell", allTools);
    }

    private static IReadOnlyList<JsonElement> ReadJsonLines(string fileName) =>
        File.ReadLines(Path.Combine(FixtureRoot, fileName))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
}
