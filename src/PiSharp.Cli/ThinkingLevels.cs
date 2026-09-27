using Microsoft.Extensions.AI;
using System.Text.Json;

namespace PiSharp.Cli;

public static class ThinkingLevels
{
    public const string Default = "medium";
    public static readonly IReadOnlyList<string> All = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    private static readonly IReadOnlyList<string> s_offOnly = ["off"];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string level)
    {
        if (!IsValid(level)) throw new ArgumentException("Thinking level must be off, minimal, low, medium, high, xhigh, or max.");
        return level.ToLowerInvariant();
    }

    public static IReadOnlyList<string> AvailableForModel(bool? supportsReasoning, JsonElement? thinkingLevelMap = null)
    {
        if (supportsReasoning != true) return s_offOnly;
        return All.Where(level =>
        {
            var mapped = GetMapValue(thinkingLevelMap, level);
            if (mapped is { ValueKind: JsonValueKind.Null }) return false;
            return level is not ("xhigh" or "max") || mapped is not null;
        }).ToArray();
    }

    public static string ValidateForModel(string level, bool? supportsReasoning, JsonElement? thinkingLevelMap = null)
    {
        level = Normalize(level);
        var available = AvailableForModel(supportsReasoning, thinkingLevelMap);
        if (available.Contains(level, StringComparer.Ordinal)) return level;
        var requestedIndex = Array.IndexOf(All.ToArray(), level);
        for (var index = requestedIndex; index < All.Count; index++)
            if (available.Contains(All[index], StringComparer.Ordinal)) return All[index];
        for (var index = requestedIndex - 1; index >= 0; index--)
            if (available.Contains(All[index], StringComparer.Ordinal)) return All[index];
        return "off";
    }

    public static ReasoningOptions? ToOptions(string level, JsonElement? thinkingLevelMap = null)
    {
        level = Normalize(level);
        if (GetMapValue(thinkingLevelMap, level) is { ValueKind: JsonValueKind.String } mapped &&
            TryGetMafLevel(mapped.GetString(), out var mappedLevel))
            level = mappedLevel;
        return level switch
        {
            "off" => null,
            "minimal" or "low" => new ReasoningOptions { Effort = ReasoningEffort.Low, Output = ReasoningOutput.Full },
            "medium" => new ReasoningOptions { Effort = ReasoningEffort.Medium, Output = ReasoningOutput.Full },
            "high" => new ReasoningOptions { Effort = ReasoningEffort.High, Output = ReasoningOutput.Full },
            "xhigh" or "max" => new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full },
            _ => throw new ArgumentException("Unknown thinking level.", nameof(level))
        };
    }

    private static JsonElement? GetMapValue(JsonElement? map, string level) =>
        map is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(level, out var mapped)
            ? mapped : null;

    private static bool TryGetMafLevel(string? mapped, out string level)
    {
        if (mapped is not null && IsValid(mapped))
        {
            level = mapped.ToLowerInvariant();
            return true;
        }
        level = "off";
        return false;
    }
}
