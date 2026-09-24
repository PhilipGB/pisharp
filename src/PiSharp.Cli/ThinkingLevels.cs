using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

public static class ThinkingLevels
{
    public static readonly IReadOnlyList<string> All = ["off", "minimal", "low", "medium", "high", "xhigh"];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string ValidateForModel(string level, bool? supportsReasoning)
    {
        if (!IsValid(level)) throw new ArgumentException("Thinking level must be off, minimal, low, medium, high, or xhigh.");
        level = level.ToLowerInvariant();
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
        "xhigh" => new ReasoningOptions { Effort = ReasoningEffort.ExtraHigh, Output = ReasoningOutput.Full },
        _ => throw new ArgumentException("Unknown thinking level.", nameof(level))
    };
}
