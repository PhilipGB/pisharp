using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Runtime.Classifiers;

namespace PiSharp.Runtime.Codemode;

internal static class CodemodeModelProjection
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static string Result(ClassifierResult result)
    {
        var value = JsonSerializer.SerializeToNode(result, s_json)!.AsObject();
        if (result.ErrorMessage is null) value.Remove("errorMessage");
        if (result.Usage is not { } usage) value.Remove("usage");
        else
        {
            var projected = new JsonObject
            {
                ["input"] = usage.InputTokens,
                ["output"] = usage.OutputTokens,
                ["cacheRead"] = usage.CachedInputTokens,
                ["cacheWrite"] = usage.CachedWriteTokens,
                ["totalTokens"] = usage.TotalTokens,
                ["cost"] = new JsonObject
                {
                    ["input"] = usage.InputCost,
                    ["output"] = usage.OutputCost,
                    ["cacheRead"] = usage.CachedInputCost,
                    ["cacheWrite"] = usage.CachedWriteCost,
                    ["total"] = usage.Cost
                }
            };
            if (usage.ReasoningTokens > 0) projected["reasoning"] = usage.ReasoningTokens;
            value["usage"] = projected;
        }
        return value.ToJsonString(s_json);
    }
}
