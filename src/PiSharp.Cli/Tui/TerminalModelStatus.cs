using System.Globalization;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Cli.Tui;

internal static class TerminalModelStatus
{
    public static string FormatIdleFooter(ModelDescriptor model, string thinking, ConversationSession session,
        int? contextWindow, bool autoCompactionEnabled, int width)
    {
        var window = contextWindow ?? model.ContextLength;
        var latest = session.LatestContextUsageTokens();
        var afterCompaction = session.Tree.ActivePath().Any(entry => entry.Type == "compaction");
        var messages = session.ActiveMessages();
        long? used = latest ?? (afterCompaction ? null : messages.Count == 0 ? 0 : SessionStatistics.Calculate(session).EstimatedContextTokens);
        var context = used is long count && window is > 0
            ? $"{(count * 100d / window.Value).ToString("F1", CultureInfo.InvariantCulture)}%/{FormatTokens(window.Value)}"
            : $"?/{FormatTokens(window ?? 0)}";
        if (autoCompactionEnabled) context += " (auto)";

        var usage = session.ActiveUsage();
        var stats = new List<string>();
        var input = usage.Sum(item => item.InputTokens);
        var output = usage.Sum(item => item.OutputTokens);
        var cached = usage.Sum(item => item.CachedInputTokens);
        var cachedWrite = usage.Sum(item => item.CachedWriteTokens);
        if (input > 0) stats.Add("↑" + FormatTokens(input));
        if (output > 0) stats.Add("↓" + FormatTokens(output));
        if (cached > 0) stats.Add("R" + FormatTokens(cached));
        if (cachedWrite > 0) stats.Add("W" + FormatTokens(cachedWrite));
        if (cached > 0 && usage.LastOrDefault(item => item.Source == "model") is { TotalTokens: > 0 } latestUsage)
            stats.Add("CH" + (cached * 100d / Math.Max(1, latestUsage.InputTokens + cached)).ToString("F1", CultureInfo.InvariantCulture) + "%");
        var cost = session.ActiveUsage().All(item => item.Cost is not null) ? usage.Sum(item => item.Cost ?? 0) : 0;
        if (cost != 0) stats.Add("$" + cost.ToString("0.000", CultureInfo.InvariantCulture));
        stats.Add(context);

        var left = string.Join(" ", stats);
        var right = model.Id;
        if (model.Reasoning == true)
            right += thinking == "off" ? " • thinking off" : " • " + thinking;
        var leftWidth = TerminalTextLayout.Width(left);
        var rightWidth = TerminalTextLayout.Width(right);
        var remaining = Math.Max(0, width - leftWidth - rightWidth);
        if (remaining < 2) return left + " " + TerminalTranscriptViewport.Clip(right, Math.Max(0, width - leftWidth - 1));
        return left + new string(' ', remaining) + right;
    }

    public static string Format(string provider, ModelDescriptor model, string thinking,
        ConversationSession session, int? contextWindow)
    {
        var selected = $"{provider}/{model.Id} · thinking {thinking}";
        if (model.Api != VirtualModelContract.Api) return selected;
        var response = session.ActiveMessages().LastOrDefault(message => message.Role == ChatRole.Assistant &&
            message.AdditionalProperties is { } properties &&
            ChatMessageProperties.String(properties, "pisharp.stopReason") is not ("error" or "aborted") &&
            ChatMessageProperties.String(properties, "pisharp.model") is not null);
        if (response?.AdditionalProperties is not { } physical) return selected;
        var routed = ChatMessageProperties.String(physical, "pisharp.model");
        var level = ChatMessageProperties.String(physical, "pisharp.thinkingLevel");
        return selected + $" → {routed}" + (level is null ? "" : $" · thinking {level}") +
            (contextWindow is > 0 ? $" · context {contextWindow.Value.ToString("N0", CultureInfo.InvariantCulture)}" : "");
    }

    private static string FormatTokens(long count)
    {
        if (count < 1000) return count.ToString(CultureInfo.InvariantCulture);
        if (count < 10_000) return (count / 1000d).ToString("F1", CultureInfo.InvariantCulture) + "k";
        if (count < 1_000_000) return Math.Round(count / 1000d).ToString(CultureInfo.InvariantCulture) + "k";
        if (count < 10_000_000) return (count / 1_000_000d).ToString("F1", CultureInfo.InvariantCulture) + "M";
        return Math.Round(count / 1_000_000d).ToString(CultureInfo.InvariantCulture) + "M";
    }
}
