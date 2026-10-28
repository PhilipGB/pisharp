using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Cli.Protocols;
using PiSharp.Cli.Tui;
using PiSharp.Runtime.Codemode;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

internal static class C10Oracle
{
    public static object Run(string workingDirectory)
    {
        var summarySession = CreateSummarySession(workingDirectory, out var branchSummary);
        var summaryUsage = summarySession.ActiveUsage().Single(usage => usage.Source == "branch_summary");

        var fallback = CreateUsage("fallback", new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 3,
            CachedInputTokenCount = 4,
            TotalTokenCount = 0,
            AdditionalCounts = CacheWrite(5)
        });
        var distinct = CreateUsage("distinct", new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 3,
            CachedInputTokenCount = 4,
            TotalTokenCount = 18,
            ReasoningTokenCount = 2,
            AdditionalCounts = CacheWrite(5)
        });
        var missing = CreateUsage("missing", new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = null,
            CachedInputTokenCount = null,
            TotalTokenCount = null
        });
        var unpriced = CreateUsage("unpriced", new UsageDetails
        {
            InputTokenCount = 10,
            OutputTokenCount = 3,
            CachedInputTokenCount = 4,
            TotalTokenCount = 18,
            AdditionalCounts = CacheWrite(5)
        });
        var priced = CreateUsage("priced", new UsageDetails
        {
            InputTokenCount = 100,
            OutputTokenCount = 3,
            CachedInputTokenCount = 25,
            TotalTokenCount = 110,
            AdditionalCounts = CacheWrite(7)
        }, new ModelPricing(3m, 8m, CachedInput: 1m, CachedWrite: 4m));
        var tiered = CreateUsage("tiered", new UsageDetails
        {
            InputTokenCount = 260_000,
            OutputTokenCount = 0,
            CachedInputTokenCount = 200_000,
            TotalTokenCount = 0,
            AdditionalCounts = CacheWrite(20_000)
        }, new ModelPricing(1m, 3m, CachedInput: 0.5m, CachedWrite: 2m,
            Tiers: [new ModelPricingTier(272_000, 10m, 30m, CachedInput: 1m, CachedWrite: 5m)]));

        var physical = new[]
        {
            CreateUsage("summary-small", new UsageDetails
            {
                InputTokenCount = 70, OutputTokenCount = 5, CachedInputTokenCount = 10,
                TotalTokenCount = 77, AdditionalCounts = CacheWrite(2)
            }, new ModelPricing(2m, 4m, CachedInput: 1m, CachedWrite: 3m)),
            CreateUsage("summary-large", new UsageDetails
            {
                InputTokenCount = 45, OutputTokenCount = 2, CachedInputTokenCount = 5,
                TotalTokenCount = 50, AdditionalCounts = CacheWrite(3)
            }, new ModelPricing(6m, 12m, CachedInput: 2m, CachedWrite: 8m))
        };
        var aggregate = CompactionUsageAccounting.Aggregate(physical)!;

        var branchUsage = CreateUsage("inactive", new UsageDetails
        {
            InputTokenCount = 100, OutputTokenCount = 10, CachedInputTokenCount = 0, TotalTokenCount = 110,
            AdditionalCounts = CacheWrite(0)
        }, new ModelPricing(1m, 1m));
        var activeBranchUsage = CreateUsage("active", new UsageDetails
        {
            InputTokenCount = 40, OutputTokenCount = 8, CachedInputTokenCount = 0, TotalTokenCount = 48,
            AdditionalCounts = CacheWrite(0)
        }, new ModelPricing(1m, 1m));
        var branchSession = new ConversationSession(workingDirectory, "fixture", null);
        branchSession.Append(new ChatMessage(ChatRole.User, "root"));
        var branchPoint = branchSession.Tree.HeadId!;
        branchSession.Append(new ChatMessage(ChatRole.Assistant, "root answer"));
        branchSession.AppendUsage(branchUsage);
        branchSession.Tree.Select(branchPoint);
        branchSession.Append(new ChatMessage(ChatRole.User, "active branch"));
        branchSession.Append(new ChatMessage(ChatRole.Assistant, "active answer"));
        branchSession.AppendUsage(activeBranchUsage);
        var activeUsage = branchSession.ActiveUsage();

        var surfaceSession = new ConversationSession(workingDirectory, "fixture", null, "fixture");
        surfaceSession.Append(new ChatMessage(ChatRole.User, "usage projections"));
        surfaceSession.Append(new ChatMessage(ChatRole.Assistant, "usage recorded"));
        surfaceSession.AppendUsage(new UsageRecord("fixture", "model", 40, 8, 5, 2, 60, 0.0123m,
            CachedWriteTokens: 7));
        var statistics = SessionStatistics.Calculate(surfaceSession);
        var footer = TerminalModelStatus.FormatIdleFooter(new ModelDescriptor("fixture", null, 1_000, null),
            "off", surfaceSession, 1_000, autoCompactionEnabled: false, width: 140);
        var rpc = RpcSessionStatsProjector.Project(surfaceSession, null, null, null);
        var rpcTokens = rpc["tokens"]!.AsObject();

        var rpcBranchSummaryUsage = CreateUsage("rpc-summary", new UsageDetails
        {
            InputTokenCount = 25,
            OutputTokenCount = 4,
            CachedInputTokenCount = 5,
            TotalTokenCount = 31,
            AdditionalCounts = CacheWrite(2)
        }, new ModelPricing(100m, 750m, CachedInput: 800m, CachedWrite: 2_500m));
        var rpcBranchSummarySession = new ConversationSession(workingDirectory, "fixture", null);
        rpcBranchSummarySession.Append(new ChatMessage(ChatRole.User, "question"));
        rpcBranchSummarySession.AppendBranchSummary(rpcBranchSummarySession.Tree.HeadId!, "generated summary",
            usage: rpcBranchSummaryUsage);
        var rpcBranchSummary = RpcSessionStatsProjector.Project(rpcBranchSummarySession, null, null, null);
        var rpcBranchSummaryTokens = rpcBranchSummary["tokens"]!.AsObject();

        return new
        {
            cases = new Dictionary<string, object>
            {
                ["C10.summary-request-usage-persists-on-active-branch"] = new
                {
                    summaryEntryType = branchSummary.Type,
                    summaryEntryIsActive = summarySession.Tree.ActivePath().Any(entry => entry.Id == branchSummary.Id),
                    usageIsOnActiveBranch = summarySession.ActiveUsage().Contains(summaryUsage),
                    input = summaryUsage.InputTokens,
                    output = summaryUsage.OutputTokens,
                    cacheRead = summaryUsage.CachedInputTokens,
                    cacheWrite = summaryUsage.CachedWriteTokens,
                    totalTokens = summaryUsage.TotalTokens
                },
                ["C10.fallback-context-total-includes-cache-tokens"] = new
                {
                    input = fallback.InputTokens,
                    output = fallback.OutputTokens,
                    cacheRead = fallback.CachedInputTokens,
                    cacheWrite = fallback.CachedWriteTokens,
                    contextTokens = fallback.TotalTokens
                },
                ["C10.input-output-cache-read-cache-write-remain-distinct"] = new
                {
                    input = distinct.InputTokens,
                    output = distinct.OutputTokens,
                    cacheRead = distinct.CachedInputTokens,
                    cacheWrite = distinct.CachedWriteTokens,
                    totalTokens = distinct.TotalTokens
                },
                ["C10.reasoning-tokens-are-preserved"] = new
                {
                    reasoning = distinct.ReasoningTokens,
                    input = distinct.InputTokens,
                    output = distinct.OutputTokens
                },
                ["C10.missing-usage-fields-remain-unknown"] = new
                {
                    inputIsUnknown = missing.InputTokens is null,
                    outputIsUnknown = missing.OutputTokens is null,
                    cacheReadIsUnknown = missing.CachedInputTokens is null,
                    cacheWriteIsUnknown = missing.CachedWriteTokens is null,
                    totalIsUnknown = missing.TotalTokens is null,
                    reasoningIsUnknown = missing.ReasoningTokens is null,
                    inputTotalIsKnown = missing.InputIncludingCachedTokens is not null
                },
                ["C10.unknown-prices-do-not-create-cost"] = new
                {
                    cost = unpriced.Cost
                },
                ["C10.known-pricing-and-input-tiers-are-calculated"] = new
                {
                    knownCost = priced.Cost,
                    tieredCost = tiered.Cost,
                    tieredInput = tiered.InputTokens,
                    tieredCacheRead = tiered.CachedInputTokens,
                    tieredCacheWrite = tiered.CachedWriteTokens
                },
                ["C10.split-summary-physical-model-costs-aggregate-correctly"] = new
                {
                    modelCount = physical.Select(usage => usage.Model).Distinct(StringComparer.Ordinal).Count(),
                    input = aggregate.InputTokens,
                    output = aggregate.OutputTokens,
                    cacheRead = aggregate.CachedInputTokens,
                    cacheWrite = aggregate.CachedWriteTokens,
                    totalTokens = aggregate.TotalTokens,
                    cost = aggregate.Cost
                },
                ["C10.inactive-branch-usage-is-excluded-from-active-total"] = new
                {
                    activeUsageCount = activeUsage.Count,
                    input = activeUsage.Sum(usage => usage.InputTokens),
                    output = activeUsage.Sum(usage => usage.OutputTokens),
                    totalTokens = activeUsage.Sum(usage => usage.TotalTokens),
                    inactiveUsageIncluded = activeUsage.Contains(branchUsage)
                },
                ["C10.cli-tui-and-rpc-report-canonical-session-usage"] = new
                {
                    cli = new { input = statistics.BilledTokens, cost = statistics.Cost },
                    tui = new
                    {
                        input = footer.Contains("↑40", StringComparison.Ordinal),
                        output = footer.Contains("↓8", StringComparison.Ordinal),
                        cacheRead = footer.Contains("R5", StringComparison.Ordinal),
                        cacheWrite = footer.Contains("W7", StringComparison.Ordinal),
                        cost = footer.Contains("$0.012", StringComparison.Ordinal)
                    },
                    rpc = new
                    {
                        input = rpcTokens["input"]!.GetValue<long>(),
                        output = rpcTokens["output"]!.GetValue<long>(),
                        cacheRead = rpcTokens["cacheRead"]!.GetValue<long>(),
                        cacheWrite = rpcTokens["cacheWrite"]!.GetValue<long>(),
                        total = rpcTokens["total"]!.GetValue<long>(),
                        cost = rpc["cost"]!.GetValue<decimal>()
                    },
                    nativeBranchSummaryRpc = new
                    {
                        summaryEntries = rpcBranchSummarySession.Tree.Entries.Count(entry => entry.Type == "branch_summary"),
                        input = rpcBranchSummaryTokens["input"]!.GetValue<long>(),
                        output = rpcBranchSummaryTokens["output"]!.GetValue<long>(),
                        cacheRead = rpcBranchSummaryTokens["cacheRead"]!.GetValue<long>(),
                        cacheWrite = rpcBranchSummaryTokens["cacheWrite"]!.GetValue<long>(),
                        total = rpcBranchSummaryTokens["total"]!.GetValue<long>(),
                        cost = rpcBranchSummary["cost"]!.GetValue<decimal>()
                    }
                }
            }
        };
    }

    private static ConversationSession CreateSummarySession(string workingDirectory, out ConversationNode summary)
    {
        var session = new ConversationSession(workingDirectory, "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "shared root"));
        session.Append(new ChatMessage(ChatRole.Assistant, "root answer"));
        var branchPoint = session.Tree.HeadId!;
        session.Append(new ChatMessage(ChatRole.User, "old branch"));
        session.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
        var oldHead = session.Tree.HeadId!;
        session.Tree.Select(branchPoint);
        session.Append(new ChatMessage(ChatRole.User, "target branch"));
        session.Append(new ChatMessage(ChatRole.Assistant, "target answer"));
        session.Tree.Select(oldHead);
        var usage = CreateUsage("summary", new UsageDetails
        {
            InputTokenCount = 25,
            OutputTokenCount = 4,
            CachedInputTokenCount = 5,
            TotalTokenCount = 34,
            AdditionalCounts = CacheWrite(0)
        });
        summary = session.AppendBranchSummary(oldHead, "branch summary", usage: usage);
        return session;
    }

    private static UsageRecord CreateUsage(string model, UsageDetails details, ModelPricing? pricing = null) =>
        UsageRecord.Create(model, "model", details, pricing);

    private static AdditionalPropertiesDictionary<long> CacheWrite(long tokens) => new()
    {
        ["cacheWriteTokens"] = tokens
    };
}
