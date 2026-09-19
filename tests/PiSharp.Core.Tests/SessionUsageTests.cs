using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Session usage aggregation, per-model cost breakdown, and cache-waste analysis
/// (pinned usage-totals.ts + cache-stats.ts + getSessionStats), plus the /session
/// rendering in the controller.
/// </summary>
public sealed class SessionUsageTests
{
    private sealed class FixedRateSource : SessionUsage.ICacheRateSource
    {
        public double Rate { get; init; } = 0.30;
        public double GetCacheReadRate(string provider, string model) => Rate;
    }

    // --- aggregation -------------------------------------------------------------

    [Fact]
    public void AggregateSumsAssistantToolResultAndSummaryUsage()
    {
        var entries = new List<SessionEntry>
        {
            UserMessage("u1", null),
            // input 1000 + cacheRead 2000 + cacheWrite 500, output 300, cost 0.5
            AssistantMessage("a1", "u1", ts: 1_700_000_000_000, "p", "m",
                input: 1_000, cacheRead: 2_000, cacheWrite: 500, output: 300,
                costInput: 0.4, costCacheRead: 0.05, costCacheWrite: 0.02, costTotal: 0.5),
            ToolResult("t1", "a1", ts: 1_700_000_000_100,
                input: 10, output: 5, costTotal: 0.01),
            new CompactionEntry("c1", "t1", DateTimeOffset.UnixEpoch, "summary", "a1", 5_000,
                Usage: Usage(input: 100, costTotal: 0.02)),
            new BranchSummaryEntry("b1", "c1", DateTimeOffset.UnixEpoch, "a1", "summary",
                Usage: Usage(input: 50, costTotal: 0.01)),
        };

        var totals = SessionUsage.Aggregate(entries);

        Assert.Equal(1_160, totals.Input);
        Assert.Equal(305, totals.Output);
        Assert.Equal(2_000, totals.CacheRead);
        Assert.Equal(500, totals.CacheWrite);
        Assert.Equal(3_965, totals.TotalTokens);
        Assert.Equal(0.54, totals.Cost, precision: 10);
    }

    // --- cost breakdown ------------------------------------------------------------

    [Fact]
    public void CostBreakdownGroupsByModelAndToolsSummaries()
    {
        var entries = new List<SessionEntry>
        {
            // responseModel wins over the requested model (pinned OpenRouter auto case).
            AssistantMessage("a1", null, ts: 1, "p", "m", input: 1_000, output: 100,
                costTotal: 0.5, responseModel: "concrete"),
            AssistantMessage("a2", "a1", ts: 2, "q", "n", input: 2_000, output: 200,
                costTotal: 0.9),
            AssistantMessage("a3", "a2", ts: 3, "p", "m", input: 100, output: 10,
                costTotal: 0.3),
            ToolResult("t1", "a2", ts: 4, input: 10, output: 5, costTotal: 0.01),
            new CompactionEntry("c1", "a3", DateTimeOffset.UnixEpoch, "s", "a1", 1,
                Usage: Usage(input: 20, costTotal: 0.005)),
            // Zero usage: dropped from the breakdown (pinned cost>0 || tokens>0 filter).
            AssistantMessage("a4", "c1", ts: 5, "z", "none", input: 0, output: 0, costTotal: 0),
        };

        var rows = SessionUsage.CostBreakdown(entries);

        Assert.Equal(
            ["q/n", "p/concrete", "p/m", "Tools/summaries"],
            rows.Select(row => row.Key).ToArray());
        Assert.Equal(0.9, rows[0].Cost, precision: 10);
        Assert.Equal(2_200, rows[0].Tokens);
        // Tools/summaries: tool result (0.015) + compaction (0.005).
        Assert.Equal(0.015, rows[3].Cost, precision: 10);
        Assert.Equal(35, rows[3].Tokens);
    }

    // --- cache waste ---------------------------------------------------------------

    [Fact]
    public void CacheWasteCountsRebilledPromptsAfterCacheWasReported()
    {
        var entries = new List<SessionEntry>
        {
            // Turn 1: no cache reported (prompt 10,000 all input).
            AssistantMessage("a1", null, ts: 1_000, "p", "m", input: 10_000, output: 100,
                costInput: 0.03, costTotal: 0.033),
            // Turn 2: still no cache reported — a zero-cache turn with no prior cache
            // activity is NOT a miss (provider without cache support).
            AssistantMessage("a2", "a1", ts: 2_000, "p", "m", input: 10_000, output: 100,
                costInput: 0.03, costTotal: 0.033),
            // Turn 3: cache reads reported (sticky flag set for subsequent turns).
            AssistantMessage("a3", "a2", ts: 3_000, "p", "m", input: 2_000, cacheRead: 8_000,
                output: 100, costInput: 0.006, costCacheRead: 0.0024, costTotal: 0.0084),
            // Turn 4: total miss after cache activity was reported — 10,000 re-billed.
            AssistantMessage("a4", "a3", ts: 4_000, "p", "m", input: 10_000, output: 100,
                costInput: 0.03, costTotal: 0.033),
        };

        var waste = SessionUsage.ComputeCacheWaste(entries, new FixedRateSource { Rate = 0.30 });

        // Two counted misses: turn 3 re-billed 2,000 tokens of the previous prompt
        // (10,000 - 8,000 served from cache) and turn 4 re-billed all 10,000.
        Assert.Equal(2, waste.MissCount);
        Assert.Equal(12_000, waste.MissedTokens);
        // paid $3/M vs read $0.30/M: 2,000 * 2.7e-6 + 10,000 * 2.7e-6 = $0.0324
        Assert.Equal(0.0324, waste.MissedCost, precision: 6);
    }

    [Fact]
    public void CacheWasteResetsAtCompactionAndDropsTheNoiseFloor()
    {
        var entries = new List<SessionEntry>
        {
            // Reports cache activity (sticky), prompt 10,000.
            AssistantMessage("a1", null, ts: 1_000, "p", "m", input: 2_000, cacheRead: 8_000,
                output: 10, costInput: 0.006, costCacheRead: 0.0024, costTotal: 0.0084),
            // Miss of exactly the noise floor (10,000 - 8,976 = 1,024) is dropped.
            AssistantMessage("a2", "a1", ts: 2_000, "p", "m", input: 1_024, cacheRead: 8_976,
                output: 10, costInput: 0.003072, costCacheRead: 0.0026928, costTotal: 0.005765),
            // One token above the floor (10,000 - 8,975 = 1,025) is counted.
            AssistantMessage("a3", "a2", ts: 3_000, "p", "m", input: 1_025, cacheRead: 8_975,
                output: 10, costInput: 0.003075, costCacheRead: 0.0026925, costTotal: 0.005768),
            new CompactionEntry("c1", "a3", DateTimeOffset.UnixEpoch, "s", "a1", 9_000,
                Usage: Usage(input: 0, costTotal: 0)),
            // After the compaction reset there is no previous request: no miss.
            AssistantMessage("a4", "c1", ts: 4_000, "p", "m", input: 10_000, output: 10,
                costInput: 0.03, costTotal: 0.03),
        };

        var waste = SessionUsage.ComputeCacheWaste(entries, new FixedRateSource());

        Assert.Equal(1, waste.MissCount);
        Assert.Equal(1_025, waste.MissedTokens);
        // 1,025 * (3.075e-6 - 3e-7)... paidPerToken = 0.003075/1025 = 3e-6, read = 3e-7.
        Assert.Equal(1_025 * 2.7e-6, waste.MissedCost, precision: 6);
    }

    // --- /session rendering -----------------------------------------------------------

    [Fact]
    public async Task SessionStatsRendersThePinnedBlock()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await SessionLifecycleTests.Harness.CreateControllerAsync(temp);
        var document = controller.Document!;

        await controller.Store!.AppendEntriesAsync(document,
        [
            // p/m1: prompt 10,000 (2,000 input + 8,000 cached), output 100.
            AssistantMessage("a1", null, ts: 1_000, "p", "m1", input: 2_000, cacheRead: 8_000,
                output: 100, costInput: 0.006, costCacheRead: 0.0024, costOutput: 0.0006,
                costTotal: 0.009),
            // p/m2: total miss — 10,000 re-billed; the harness runtime has no "p"
            // provider, so the read rate falls back to 0 (pinned `?? 0`).
            AssistantMessage("a2", "a1", ts: 2_000, "p", "m2", input: 10_000,
                output: 50, costInput: 0.03, costOutput: 0.003, costTotal: 0.033),
        ], CancellationToken.None);

        var output = controller.FormatSessionStats();

        Assert.Contains("Session Info", output);
        Assert.Contains("Messages", output);
        Assert.Contains("Assistant: 2", output);
        Assert.Contains("Input: 20,000", output);
        Assert.Contains("  Cached: 8,000 (40.0%)", output);
        Assert.Contains("  Uncached: 12,000", output);
        Assert.Contains("Output: 150", output);
        Assert.Contains("Total: 20,150", output);
        Assert.Contains("Total: $0.042", output);
        // Two attributed models: the per-model rows appear, cost-descending.
        var m2Line = output.IndexOf("p/m2:", StringComparison.Ordinal);
        var m1Line = output.IndexOf("p/m1:", StringComparison.Ordinal);
        Assert.True(m2Line >= 0 && m1Line >= 0 && m2Line < m1Line, output);
        Assert.Contains("Cache Re-billed: $0.030 (10,000 tokens, 1 miss)", output);
    }

    [Fact]
    public async Task SessionStatsOmitsTheCostSectionWithoutUsage()
    {
        using var temp = TempDirectory.Create();
        var (controller, _) = await SessionLifecycleTests.Harness.CreateControllerAsync(temp);
        await controller.Store!.AppendEntriesAsync(controller.Document!,
        [
            AssistantMessage("a1", null, ts: 1, "p", "m", input: 100, output: 10, costTotal: 0),
        ], CancellationToken.None);

        var output = controller.FormatSessionStats();

        Assert.Contains("Tokens", output);
        Assert.DoesNotContain("Cost", output);
        Assert.DoesNotContain("Cache Re-billed", output);
    }

    // --- fixtures ------------------------------------------------------------------------

    private static JsonElement Usage(
        long input = 0, long output = 0, long cacheRead = 0, long cacheWrite = 0, double costTotal = 0) =>
        JsonSerializer.SerializeToElement(new
        {
            input, output, cacheRead, cacheWrite,
            totalTokens = input + output + cacheRead + cacheWrite,
            cost = new { input = 0.0, output = 0.0, cacheRead = 0.0, cacheWrite = 0.0, total = costTotal },
        });

    private static MessageEntry UserMessage(string id, string? parentId) =>
        new(id, parentId, DateTimeOffset.UnixEpoch,
            JsonSerializer.SerializeToElement(new { role = "user", content = "hi" }));

    private static MessageEntry ToolResult(
        string id, string? parentId, long ts, long input, long output, double costTotal) =>
        new(id, parentId, DateTimeOffset.UnixEpoch,
            JsonSerializer.SerializeToElement(new
            {
                role = "toolResult",
                toolCallId = "call-1",
                toolName = "bash",
                content = new[] { new { type = "text", text = "ok" } },
                timestamp = ts,
                usage = Usage(input, output, costTotal: costTotal),
            }));

    private static MessageEntry AssistantMessage(
        string id,
        string? parentId,
        long ts,
        string provider,
        string model,
        long input = 0,
        long cacheRead = 0,
        long cacheWrite = 0,
        long output = 0,
        double costInput = 0.0,
        double costCacheRead = 0.0,
        double costCacheWrite = 0.0,
        double costOutput = 0.0,
        double costTotal = 0.0,
        string? responseModel = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["provider"] = provider,
            ["model"] = model,
            ["content"] = new object[] { new { type = "text", text = "answer" } },
            ["timestamp"] = ts,
        };
        if (responseModel is not null)
        {
            message["responseModel"] = responseModel;
        }

        // The usage element carries the per-bucket cost breakdown the waste scan needs.
        var usage = JsonSerializer.SerializeToElement(new
        {
            input, output, cacheRead, cacheWrite,
            totalTokens = input + output + cacheRead + cacheWrite,
            cost = new
            {
                input = costInput,
                output = costOutput,
                cacheRead = costCacheRead,
                cacheWrite = costCacheWrite,
                total = costTotal,
            },
        });
        message["usage"] = usage;

        return new MessageEntry(id, parentId, DateTimeOffset.UnixEpoch,
            JsonSerializer.SerializeToElement(message));
    }
}
