namespace PiSharp.Core.Models;

/// <summary>
/// Pi thinking levels in canonical order (pinned Pi: packages/ai/src/types.ts).
/// "off" is not a <see cref="ThinkingLevel"/> in Pi; it is the non-reasoning state.
/// </summary>
public static class ThinkingLevel
{
    /// <summary>Canonical order: off, minimal, low, medium, high, xhigh, max.</summary>
    public static readonly string[] All =
    {
        "off", "minimal", "low", "medium", "high", "xhigh", "max",
    };

    /// <summary>Thinking levels selectable for reasoning models (Pi: THINKING_LEVEL_OPTIONS).</summary>
    public static readonly string[] Options =
    {
        "minimal", "low", "medium", "high", "xhigh", "max",
    };

    /// <summary>Default thinking level (pinned Pi: core/defaults.ts).</summary>
    public const string Default = "medium";

    /// <summary>Returns true when the value is a known thinking level.</summary>
    public static bool IsValid(string? level) =>
        level is not null && All.Contains(level, StringComparer.Ordinal);
}

/// <summary>
/// Pi thinking-level support and clamping (ported from pinned pi-ai models.ts:
/// getSupportedThinkingLevels / clampThinkingLevel).
/// </summary>
public static class ThinkingLevelSupport
{
    private static readonly string[] ExtendedLevels = ThinkingLevel.All;

    /// <summary>
    /// Levels the model supports. Non-reasoning models support only "off".
    /// A null map entry marks a level unsupported; xhigh/max require an explicit mapping.
    /// </summary>
    public static string[] GetSupportedLevels(ModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.Reasoning)
        {
            return ["off"];
        }

        return ExtendedLevels
            .Where(level =>
            {
                var mapped = model.ThinkingLevelMap is not null
                    && model.ThinkingLevelMap.TryGetValue(level, out var value)
                    ? value
                    : null;
                if (mapped is null && IsExplicitlyUnsupported(model, level))
                {
                    return false;
                }

                return level is "xhigh" or "max"
                    ? model.ThinkingLevelMap is not null && model.ThinkingLevelMap.TryGetValue(level, out _)
                    : true;
            })
            .ToArray();
    }

    /// <summary>
    /// Clamps a requested level to the nearest supported level: higher supported levels first,
    /// then lower, then the first supported level (Pi clampThinkingLevel semantics).
    /// </summary>
    public static string Clamp(ModelInfo model, string level)
    {
        ArgumentNullException.ThrowIfNull(model);
        var available = GetSupportedLevels(model);
        if (available.Contains(level, StringComparer.Ordinal))
        {
            return level;
        }

        var requestedIndex = ExtendedLevels.IndexOf(level, StringComparer.Ordinal);
        if (requestedIndex < 0)
        {
            return available.Length > 0 ? available[0] : "off";
        }

        for (var index = requestedIndex; index < ExtendedLevels.Length; index++)
        {
            if (available.Contains(ExtendedLevels[index], StringComparer.Ordinal))
            {
                return ExtendedLevels[index];
            }
        }

        for (var index = requestedIndex - 1; index >= 0; index--)
        {
            if (available.Contains(ExtendedLevels[index], StringComparer.Ordinal))
            {
                return ExtendedLevels[index];
            }
        }

        return available.Length > 0 ? available[0] : "off";
    }

    private static bool IsExplicitlyUnsupported(ModelInfo model, string level) =>
        model.ThinkingLevelMap is { } map
        && map.TryGetValue(level, out var value)
        && value is null;
}
