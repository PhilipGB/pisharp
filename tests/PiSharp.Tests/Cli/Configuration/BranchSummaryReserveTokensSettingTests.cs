using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using Microsoft.Extensions.AI;

namespace PiSharp.Tests;

public sealed class BranchSummaryReserveTokensSettingTests
{
    public static IEnumerable<object[]> PinnedCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "reserve-cases.json")));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(item => new object[] { item.GetProperty("id").GetString()!, item.Clone() }).ToArray();
    }

    [Theory]
    [MemberData(nameof(PinnedCases))]
    public async Task RawSettingsThroughActualProviderRequestMatchPinnedPi(string id, JsonElement item)
    {
        var oracle = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "reserve-oracle.json")))!;
        var expected = oracle["pi"]!.AsArray().Single(value => value!["id"]!.GetValue<string>() == id)!;
        var actual = JsonSerializer.SerializeToNode(await BranchSummaryReserveProbe.ObserveAsync(item))!;
        var raw = actual["raw"]?.GetValue<string>();
        if (raw is not null && raw != "<absent>") actual["raw"] = JsonNode.Parse(raw);
        foreach (var property in expected.AsObject())
        {
            if (property.Key == "conversation") continue;
            Assert.True(JsonNode.DeepEquals(property.Value, actual[property.Key]),
                $"{id}.{property.Key}: expected {property.Value}, actual {actual[property.Key]}");
        }
    }

    [Theory]
    [InlineData("BR-zero", 1024, 1024)]
    [InlineData("BR-normal", 8192, 4096)]
    [InlineData("BR-normal", 0, 4096)]
    public async Task SummaryOutputCapIsIndependentOfHistoryReserve(string id, int modelMax, int expectedCap)
    {
        var source = PinnedCases().Single(item => (string)item[0] == id);
        var item = JsonNode.Parse(((JsonElement)source[1]).GetRawText())!;
        item["maxTokens"] = modelMax;
        var actual = JsonSerializer.SerializeToNode(await BranchSummaryReserveProbe.ObserveAsync(
            JsonSerializer.SerializeToElement(item)))!;
        Assert.Null(actual["error"]);
        Assert.Equal(expectedCap, actual["outputCaps"]![0]!.GetValue<int>());
    }

    [Fact]
    public void RawReserveObjectsDeepMergeByPresenceWithoutChangingSkipPrompt()
    {
        static BranchSummarySettingValue Parse(string text) => new(BranchSummarySettings.Parse(JsonDocument.Parse(text).RootElement));
        var user = new UserSettings(BranchSummary: Parse("{\"reserveTokens\":{\"a\":1},\"skipPrompt\":true}"));
        var project = new UserSettings(BranchSummary: Parse("{\"reserveTokens\":{\"b\":2}}"));
        var merged = user.Overlay(project).EffectiveBranchSummary;
        Assert.True(merged.SkipsSummaryPrompt);
        Assert.True(double.IsNaN(merged.EffectiveReserveTokens));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"a\":1,\"b\":2}"), JsonNode.Parse(merged.RawReserveTokens!.Value.Raw)));
    }

    [Fact]
    public async Task StronglyTypedNavigationStillRejectsNegativeReserve()
    {
        var root = Path.Combine(Path.GetTempPath(), "reserve-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var conversation = new ConversationSession(root, "fixture", null);
            var run = await ConversationRun.OpenAsync(new PiAgent(new NeverCalledClient(), new CodingTools(root)), conversation);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => run.NavigateTreeAsync(null,
                new ConversationTreeSelectionOptions(ReserveTokens: -1)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(100, 80, 20)]
    [InlineData(100, 100, 0)]
    [InlineData(100, 101, -1)]
    [InlineData(32, 2.5, 29.5)]
    [InlineData(0, 127980, 20)]
    public void HistoryBudgetRetainsPinnedArithmetic(int context, double reserve, double budget) =>
        Assert.Equal(budget, new ConversationTreeSelectionOptions(ConfiguredReserveTokens: reserve).HistoryTokenBudget(context));

    private sealed class NeverCalledClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
