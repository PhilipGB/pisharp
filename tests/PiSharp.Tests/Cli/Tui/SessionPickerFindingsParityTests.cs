using System.Text.Json;

namespace PiSharp.Tests;

/// <summary>Pins the post-merge F01-F03 findings to the paired-terminal oracle.</summary>
public sealed class SessionPickerFindingsParityTests
{
    private static readonly string OraclePath = Path.Combine(RepositoryRoot(), "docs", "parity", "fixtures",
        "session-picker-post-merge-oracle-2026-11-04.json");

    [Fact]
    public void OracleInventoryNamesPinnedPiSourceAnchorsAndExactlyF01ToF03()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(OraclePath));
        var root = document.RootElement;
        Assert.Equal("session-picker-post-merge-findings", root.GetProperty("capability").GetString());
        Assert.Equal("b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6", root.GetProperty("piSha").GetString());
        var paths = root.GetProperty("sources").EnumerateArray()
            .Select(source => source.GetProperty("path").GetString()).ToArray();
        Assert.Contains("packages/coding-agent/src/modes/interactive/components/session-selector.ts", paths);
        Assert.Contains("packages/coding-agent/src/core/session-manager.ts", paths);
        Assert.Contains("packages/coding-agent/src/modes/interactive/components/keybinding-hints.ts", paths);
        var scenarios = root.GetProperty("scenarios").EnumerateObject().ToArray();
        Assert.Equal(["F01", "F02", "F03"], scenarios.Select(scenario => scenario.Name));
        Assert.All(scenarios, scenario => Assert.NotEmpty(scenario.Value.GetProperty("source").EnumerateArray()));
        Assert.Equal("canonical-session-path",
            scenarios[0].Value.GetProperty("expected").GetProperty("identity").GetString());
        Assert.Equal("subtree-latest-activity-desc",
            scenarios[1].Value.GetProperty("expected").GetProperty("rootOrder").GetString());
        Assert.Equal("flat-score-order",
            scenarios[1].Value.GetProperty("expected").GetProperty("threadedWithQuery").GetString());
        Assert.Equal("none",
            scenarios[1].Value.GetProperty("expected").GetProperty("threadedWithQueryPrefixes").GetString());
        Assert.Equal("modified-desc",
            scenarios[1].Value.GetProperty("expected").GetProperty("threadedWithQueryTieBreak").GetString());
        Assert.Equal("",
            scenarios[2].Value.GetProperty("expected").GetProperty("disabledConfirmKey").GetString());
        Assert.Equal("only-without-keymap",
            scenarios[2].Value.GetProperty("expected").GetProperty("fallback").GetString());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
