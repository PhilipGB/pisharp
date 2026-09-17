using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// models-store.json (de)serialization. The file shape is a provider-id map to
/// ModelsStoreEntry objects; entries are stored with the full model list so a cold
/// start can serve cached dynamic catalogs offline.
/// </summary>
public static class ModelsStoreJson
{
    /// <summary>Serializes the store map to JSON text (2-space indent).</summary>
    public static string Serialize(IReadOnlyDictionary<string, ModelsStoreEntry> entries)
    {
        var root = new JsonObject();
        foreach (var (providerId, entry) in entries)
        {
            root[providerId] = SerializeEntry(entry);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return root.ToJsonString(options);
    }

    private static JsonObject SerializeEntry(ModelsStoreEntry entry)
    {
        var modelsArray = new JsonArray();
        var obj = new JsonObject
        {
            ["models"] = modelsArray,
            ["checkedAt"] = entry.CheckedAt,
            ["lastModified"] = entry.LastModified,
        };

        if (entry.Etag is not null)
        {
            obj["etag"] = entry.Etag;
        }

        foreach (var model in entry.Models)
        {
            modelsArray.Add(SerializeModel(model));
        }

        return obj;
    }

    private static JsonObject SerializeModel(ModelInfo model)
    {
        var obj = new JsonObject
        {
            ["id"] = model.Id,
            ["name"] = model.Name,
            ["api"] = model.Api,
            ["provider"] = model.Provider,
            ["baseUrl"] = model.BaseUrl,
            ["reasoning"] = model.Reasoning,
            ["contextWindow"] = model.ContextWindow,
            ["maxTokens"] = model.MaxTokens,
        };

        var input = new JsonArray();
        foreach (var modality in model.Input)
        {
            input.Add(modality);
        }

        obj["input"] = input;

        var cost = new JsonObject
        {
            ["input"] = model.Cost.Input,
            ["output"] = model.Cost.Output,
            ["cacheRead"] = model.Cost.CacheRead,
            ["cacheWrite"] = model.Cost.CacheWrite,
        };
        obj["cost"] = cost;

        if (model.ThinkingLevelMap is not null)
        {
            var map = new JsonObject();
            foreach (var (level, value) in model.ThinkingLevelMap)
            {
                map[level] = value is null ? JsonValue.Create((string?)null) : value;
            }

            obj["thinkingLevelMap"] = map;
        }

        if (model.Cost.Tiers is not null)
        {
            var tiers = new JsonArray();
            foreach (var tier in model.Cost.Tiers)
            {
                tiers.Add(new JsonObject
                {
                    ["inputTokensAbove"] = tier.InputTokensAbove,
                    ["input"] = tier.Input,
                    ["output"] = tier.Output,
                    ["cacheRead"] = tier.CacheRead,
                    ["cacheWrite"] = tier.CacheWrite,
                });
            }

            cost["tiers"] = tiers;
        }

        return obj;
    }

    /// <summary>Parses one entry; malformed entries resolve to null (best effort).</summary>
    public static ModelsStoreEntry? ParseEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsedModels = new List<ModelInfo>();
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                !model.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                !model.TryGetProperty("api", out var api) || api.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var baseUrl = model.TryGetProperty("baseUrl", out var baseUrlElement) && baseUrlElement.ValueKind == JsonValueKind.String
                ? baseUrlElement.GetString()!
                : string.Empty;

            var name = model.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()!
                : id.GetString()!;

            var input = new List<string> { "text" };
            if (model.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.Array)
            {
                input = inputElement.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
                    .ToList();
            }

            var cost = new ModelCost { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 };
            if (model.TryGetProperty("cost", out var costElement) && costElement.ValueKind == JsonValueKind.Object)
            {
                cost = new ModelCost
                {
                    Input = ReadDouble(costElement, "input"),
                    Output = ReadDouble(costElement, "output"),
                    CacheRead = ReadDouble(costElement, "cacheRead"),
                    CacheWrite = ReadDouble(costElement, "cacheWrite"),
                    Tiers = costElement.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array
                        ? tiers.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).Select(tier => new ModelCostTier
                        {
                            InputTokensAbove = ReadDouble(tier, "inputTokensAbove"),
                            Input = ReadDouble(tier, "input"),
                            Output = ReadDouble(tier, "output"),
                            CacheRead = ReadDouble(tier, "cacheRead"),
                            CacheWrite = ReadDouble(tier, "cacheWrite"),
                        }).ToArray()
                        : null,
                };
            }

            IReadOnlyDictionary<string, string?>? thinkingLevelMap = null;
            if (model.TryGetProperty("thinkingLevelMap", out var mapElement) && mapElement.ValueKind == JsonValueKind.Object)
            {
                thinkingLevelMap = mapElement.EnumerateObject()
                    .ToDictionary(
                        e => e.Name,
                        e => e.Value.ValueKind == JsonValueKind.String ? e.Value.GetString() : null,
                        StringComparer.Ordinal);
            }

            parsedModels.Add(new ModelInfo
            {
                Id = id.GetString()!,
                Name = name,
                Api = api.GetString()!,
                Provider = model.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String
                    ? provider.GetString()!
                    : string.Empty,
                BaseUrl = baseUrl,
                Reasoning = model.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.True,
                Input = input,
                Cost = cost,
                ContextWindow = (int)ReadDouble(model, "contextWindow"),
                MaxTokens = (int)ReadDouble(model, "maxTokens"),
                ThinkingLevelMap = thinkingLevelMap,
            });
        }

        return new ModelsStoreEntry
        {
            Models = parsedModels,
            CheckedAt = element.TryGetProperty("checkedAt", out var checkedAt) && checkedAt.ValueKind == JsonValueKind.Number
                ? checkedAt.GetInt64()
                : 0,
            LastModified = element.TryGetProperty("lastModified", out var lastModified) && lastModified.ValueKind == JsonValueKind.Number
                ? lastModified.GetInt64()
                : 0,
            Etag = element.TryGetProperty("etag", out var etag) && etag.ValueKind == JsonValueKind.String
                ? etag.GetString()
                : null,
        };
    }

    private static double ReadDouble(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;
    }
}
