using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

public static class ThinkingLevels
{
    public static readonly IReadOnlyList<string> All = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    private static readonly IReadOnlyList<string> s_offOnly = ["off"];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string level)
    {
        if (!IsValid(level)) throw new ArgumentException("Thinking level must be off, minimal, low, medium, high, xhigh, or max.");
        return level.ToLowerInvariant();
    }

    public static IReadOnlyList<string> AvailableForModel(bool? supportsReasoning) =>
        supportsReasoning == true ? All : s_offOnly;

    public static string ValidateForModel(string level, bool? supportsReasoning)
    {
        level = Normalize(level);
        if (level != "off" && supportsReasoning != true)
            throw new InvalidOperationException("The selected model does not advertise reasoning support; thinking remains off.");
        return level;
    }

    public static ReasoningOptions? ToOptions(string level) => level.ToLowerInvariant() switch
    {
        "off" => null,
        "minimal" or "low" => new ReasoningOptions { Effort = ReasoningEffort.Low, Output = ReasoningOutput.Full },
        "medium" => new ReasoningOptions { Effort = ReasoningEffort.Medium, Output = ReasoningOutput.Full },
        "high" => new ReasoningOptions { Effort = ReasoningEffort.High, Output = ReasoningOutput.Full },
        "xhigh" or "max" => new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full },
        _ => throw new ArgumentException("Unknown thinking level.", nameof(level))
    };
}
