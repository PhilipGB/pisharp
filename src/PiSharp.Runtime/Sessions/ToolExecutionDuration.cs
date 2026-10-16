using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Retains monotonic execution time on canonical results across native and Pi sessions.</summary>
internal static class ToolExecutionDuration
{
    private const string Key = "pisharp.toolDurationMs";

    internal static void Set(FunctionResultContent result, double? milliseconds)
    {
        if (milliseconds is not { } value || !double.IsFinite(value) || value < 0) return;
        result.AdditionalProperties ??= new();
        result.AdditionalProperties[Key] = value;
    }

    internal static double? Get(FunctionResultContent result)
    {
        if (result.AdditionalProperties?.TryGetValue(Key, out var stored) != true) return null;
        var value = stored switch
        {
            double number => number,
            long number => number,
            int number => number,
            JsonElement { ValueKind: JsonValueKind.Number } number when number.TryGetDouble(out var parsed) => parsed,
            _ => double.NaN
        };
        return double.IsFinite(value) && value >= 0 ? value : null;
    }
}
