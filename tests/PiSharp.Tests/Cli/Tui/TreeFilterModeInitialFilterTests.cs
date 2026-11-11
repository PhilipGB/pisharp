using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli.Tui;
using PiSharp.Cli;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

/// <summary>`treeFilterMode` supplies the initial filter of a fresh tree selector state.</summary>
public sealed class TreeFilterModeInitialFilterTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("default", new[] { "user", "assistant", "user", "result", "note" })]
    [InlineData("no-tools", new[] { "user", "assistant", "user", "note" })]
    [InlineData("user-only", new[] { "user", "user" })]
    [InlineData("labeled-only", new[] { "assistant" })]
    [InlineData("all", new[] { "model", "user", "assistant", "user", "result", "note", "label" })]
    public void InitialFilterModeSelectsThePinnedVisibleProjection(string configured, string[] expected)
    {
        var tree = FixtureTree();
        var state = new TerminalTreeState(tree.Entries, tree.HeadId, TerminalTreeFilterModes.Parse(configured));

        Assert.Equal(configured, new UserSettings(TreeFilterMode: configured).EffectiveTreeFilterMode);
        Assert.Equal(expected, state.VisibleNodes.Select(RowKind).ToArray());
    }

    [Fact]
    public void EveryPinnedSettingValueMapsToItsFilterModeAndBack()
    {
        Assert.Equal(["default", "no-tools", "user-only", "labeled-only", "all"],
            TreeFilterModeSetting.Values);
        for (var index = 0; index < TreeFilterModeSetting.Values.Length; index++)
        {
            var value = TreeFilterModeSetting.Values[index];
            Assert.Equal((TerminalTreeFilterMode)index, TerminalTreeFilterModes.Parse(value));
        }
        Assert.Equal(TerminalTreeFilterMode.Default, TerminalTreeFilterModes.Parse("bogus"));
        Assert.Equal(TerminalTreeFilterMode.Default, TerminalTreeFilterModes.Parse(null));
    }

    [Fact]
    public void FilteredLeafFallsBackToTheNearestVisibleAncestor()
    {
        var tree = FixtureTree();
        var assistant = tree.Entries.First(entry => entry.Type == "chat" &&
            ConversationMessageCodec.RestoreEntry(entry).Role == ChatRole.Assistant);
        var label = tree.Entries.Last(entry => entry.Type == "label");

        var userOnly = new TerminalTreeState(tree.Entries, label.Id, TerminalTreeFilterMode.UserOnly);
        Assert.Equal("user", RowKind(userOnly.SelectedNode!));
        Assert.Equal("user", RowKind(userOnly.VisibleNodes[userOnly.SelectedIndex]));

        var labeledOnly = new TerminalTreeState(tree.Entries, label.Id, TerminalTreeFilterMode.LabeledOnly);
        Assert.Equal(assistant.Id, labeledOnly.SelectedNode!.Node.Entry.Id);
    }

    [Fact]
    public void EmptyInitialFilterRendersNoEntriesAndKeepsAStableFallbackSelection()
    {
        var unlabeled = new ConversationTree();
        unlabeled.Append("chat", Chat(ChatRole.User, "only user"), at: Stamp(0));
        var assistant = unlabeled.Append("chat", Chat(ChatRole.Assistant, "only answer"), at: Stamp(1));

        var empty = new TerminalTreeState(unlabeled.Entries, assistant.Id, TerminalTreeFilterMode.LabeledOnly);
        Assert.Empty(empty.VisibleNodes);
        Assert.Null(empty.SelectedNode);

        var rendered = TerminalTreeView.Render(empty, TerminalTheme.Default, 60, 20, _ => "-");
        var text = string.Join("\n", rendered.Select(TerminalTextLayout.StripFormatting));
        Assert.Contains("No entries found", text);
        Assert.Contains("(0/0) [labeled]", text);
    }

    [Fact]
    public void InteractiveFilterChangesDoNotAlterTheConfiguredSource()
    {
        var tree = FixtureTree();
        var configured = new UserSettings(TreeFilterMode: "user-only").EffectiveTreeFilterMode;
        var state = new TerminalTreeState(tree.Entries, tree.HeadId,
            TerminalTreeFilterModes.Parse(configured));
        Assert.Equal(TerminalTreeFilterMode.UserOnly, state.FilterMode);

        state.CycleFilter(1);
        state.SetFilterMode(TerminalTreeFilterMode.All);
        state.AppendSearchText("question");
        state.ToggleLabelTimestamps();

        Assert.Equal(TerminalTreeFilterMode.All, state.FilterMode);
        var reopened = new TerminalTreeState(tree.Entries, tree.HeadId,
            TerminalTreeFilterModes.Parse(configured));
        Assert.Equal(TerminalTreeFilterMode.UserOnly, reopened.FilterMode);
    }

    private static string RowKind(TerminalTreeFlatNode flatNode)
    {
        var entry = flatNode.Node.Entry;
        if (entry.Type != "chat") return entry.Type switch
        {
            "model_change" => "model",
            "custom_message" => "note",
            _ => entry.Type
        };
        var role = ConversationMessageCodec.RestoreEntry(entry).Role;
        return role == ChatRole.User ? "user" : role == ChatRole.Assistant ? "assistant" : "result";
    }

    private static ConversationTree FixtureTree()
    {
        var tree = new ConversationTree();
        tree.Append("model_change", ToElement(new { model = "beta" }), at: Stamp(0));
        tree.Append("chat", Chat(ChatRole.User, "first question"), at: Stamp(1));
        var assistant = tree.Append("chat", Chat(ChatRole.Assistant, "first answer"), at: Stamp(2));
        tree.Append("chat", Chat(ChatRole.User, "second question"), at: Stamp(3));
        tree.Append("chat", ConversationMessageCodec.EncodeMessage(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call-1", "result-1")])), at: Stamp(4));
        tree.Append("chat", ConversationMessageCodec.EncodeMessage(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-2", "read", new Dictionary<string, object?> { ["path"] = "README.md" })])),
            at: Stamp(5));
        tree.Append("custom_message", ToElement(new { customType = "note", content = "note text" }), at: Stamp(6));
        tree.Append("label", ToElement(new { targetId = assistant.Id, label = "answer label" }), at: Stamp(7));
        return tree;
    }

    private static JsonElement Chat(ChatRole role, string text) =>
        ConversationMessageCodec.EncodeMessage(new ChatMessage(role, text));

    private static DateTimeOffset Stamp(int minutes) => Baseline.AddMinutes(minutes);

    private static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value);
}
