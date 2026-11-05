#!/usr/bin/env python3
"""Reproduce F01-F03 against the merged pre-fix baseline (PR #40)."""
import argparse
import subprocess
import sys
from pathlib import Path

BASELINE_SHA = "a4b51240c0426508ac4fe5c5861b7e914dd7307f"
TEST_PATH = Path("tests/PiSharp.Tests/Cli/Tui/BaselineFindingsReproductionTests.cs")
EXPECTED = {
    "F01SameIdSessionsDoNotSharePickerIdentity": ("Expected: 2", "Actual:   1"),
    "F02ThreadedModeGroupsChildrenUnderTheirParent": ('["parent", "child"]', '["child", "parent"]'),
    "F03DisabledConfirmationBindingDoesNotAdvertiseEnter": ('Found:  "Enter"',),
}
TEST_SOURCE = r'''using System.Text.Json;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class BaselineFindingsReproductionTests
{
    [Fact]
    public async Task F01SameIdSessionsDoNotSharePickerIdentity()
    {
        using var fixture = Fixture.Create();
        await fixture.WriteAsync("one.jsonl", "shared-id", "one", "2026-01-01T00:00:00Z", null);
        await fixture.WriteAsync("two.jsonl", "shared-id", "two", "2026-01-02T00:00:00Z", null);
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        var entries = state.BuildEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task F02ThreadedModeGroupsChildrenUnderTheirParent()
    {
        using var fixture = Fixture.Create();
        var parent = fixture.Path("parent.jsonl");
        await fixture.WriteAsync("parent.jsonl", "parent-id", "parent", "2026-01-01T00:00:00Z", null);
        await fixture.WriteAsync("child.jsonl", "child-id", "child", "2026-01-02T00:00:00Z", parent);
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, null, null);
        Assert.Equal(["parent", "child"], state.BuildEntries().Select(entry => entry.Listing.Name));
    }

    [Fact]
    public async Task F03DisabledConfirmationBindingDoesNotAdvertiseEnter()
    {
        using var fixture = Fixture.Create();
        await fixture.WriteAsync("current.jsonl", "current-id", "current", "2026-01-02T00:00:00Z", null);
        await fixture.WriteAsync("victim.jsonl", "victim-id", "victim", "2026-01-01T00:00:00Z", null);
        var directory = Path.Combine(fixture.Root, "agent");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "keybindings.json"), """
            {"tui.select.confirm": [], "tui.select.cancel": []}
            """);
        var keymap = new EditorKeymap(directory, windowsBindings: false);
        var state = await TerminalSessionPickerState.LoadAsync(fixture.Store, fixture.Path("current.jsonl"), null);
        var victim = state.BuildEntries().Single(entry => !entry.IsCurrent);
        Assert.True(state.BeginDelete(victim.Key));
        var hint = TerminalSessionPicker.BuildActionHint(state, keymap.GetDisplayKeys)!;
        Assert.DoesNotContain("Enter", hint);
        Assert.DoesNotContain("Escape", hint);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, ConversationStore store) { Root = root; Store = store; }
        public string Root { get; }
        public ConversationStore Store { get; }
        public string Path(string name) => System.IO.Path.Combine(Store.DirectoryPath, name);
        public static Fixture Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-baseline-findings-" + Guid.NewGuid().ToString("N"));
            var sessions = System.IO.Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessions);
            return new(root, new ConversationStore(root, sessions));
        }
        public async Task WriteAsync(string file, string id, string name, string timestamp, string? parent)
        {
            var path = Path(file);
            var parentField = parent is null ? "" : $",\"parentSession\":{JsonSerializer.Serialize(parent)}";
            await File.WriteAllTextAsync(path,
                $"{{\"type\":\"session\",\"version\":3,\"id\":{JsonSerializer.Serialize(id)},\"timestamp\":{JsonSerializer.Serialize(timestamp)},\"cwd\":{JsonSerializer.Serialize(Root)}{parentField}}}\n" +
                $"{{\"type\":\"message\",\"id\":\"{id}-message\",\"parentId\":null,\"timestamp\":{JsonSerializer.Serialize(timestamp)},\"message\":{{\"role\":\"user\",\"content\":[{{\"type\":\"text\",\"text\":{JsonSerializer.Serialize(name)}}}],\"timestamp\":0}}}}\n" +
                $"{{\"type\":\"session_info\",\"id\":\"{id}-info\",\"parentId\":\"{id}-message\",\"timestamp\":{JsonSerializer.Serialize(timestamp)},\"name\":{JsonSerializer.Serialize(name)}}}\n");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True,
                        help="Worktree checked out at the merged PR #40 baseline")
    args = parser.parse_args()
    baseline = args.baseline.resolve()
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=baseline, text=True).strip()
    if head != BASELINE_SHA:
        raise SystemExit(f"Baseline worktree is {head}, expected {BASELINE_SHA}.")
    test_path = baseline / TEST_PATH
    if test_path.exists():
        raise SystemExit(f"Refusing to overwrite {test_path}.")
    test_path.write_text(TEST_SOURCE, encoding="utf-8")
    try:
        result = subprocess.run(
            ["dotnet", "test", str(baseline / "tests/PiSharp.Tests/PiSharp.Tests.csproj"),
             "--filter", "FullyQualifiedName~BaselineFindingsReproductionTests", "--nologo", "-v", "n"],
            cwd=baseline, text=True, capture_output=True)
    finally:
        test_path.unlink()
    output = result.stdout + result.stderr
    if result.returncode == 0:
        raise SystemExit("Baseline unexpectedly passed the F01-F03 reproductions.")
    missing = []
    for name, markers in EXPECTED.items():
        if f"{name} [FAIL]" not in output:
            missing.append(f"{name} (test did not fail)")
            continue
        for marker in markers:
            if marker not in output:
                missing.append(f"{name} (missing failure marker {marker!r})")
    if missing:
        raise SystemExit("Baseline reproduction did not show the expected failures:\n" + "\n".join(missing))
    print(f"baseline {BASELINE_SHA}: F01, F02 and F03 failed as expected")
    for name in EXPECTED:
        print(f"  FAILED {name}")


if __name__ == "__main__":
    main()
