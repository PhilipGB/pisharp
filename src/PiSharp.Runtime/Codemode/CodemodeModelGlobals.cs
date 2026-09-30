using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Codemode;

internal static class CodemodeModelGlobals
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static async Task<string?> InvokeAsync(string name, JsonElement input,
        PiSharpToolExecutionContext context, CancellationToken cancellationToken)
    {
        var models = context.Models ?? throw new InvalidOperationException("Model access is unavailable in this tool session.");
        if (input.ValueKind != JsonValueKind.Array) throw new ArgumentException("Model arguments must be an array.");
        if (name == "models.classify")
        {
            if (input.GetArrayLength() != 2 || input[0].ValueKind != JsonValueKind.Object)
                throw new ArgumentException("models.classify expects a catalog model and classifier context.");
            var provider = RequiredString(input[0], "provider");
            var id = RequiredString(input[0], "id");
            if (!models.GetModels("classifier", provider).Any(model => model.GetProperty("id").GetString() == id && model.GetProperty("provider").GetString() == provider))
                throw new ArgumentException($"Unknown classifier {provider}/{id}.");
            var classifierContext = input[1].Deserialize<Classifiers.ClassifierContext>(s_json) ?? throw new ArgumentException("Classifier context is required.");
            var result = await context.ClassifyModelAsync(provider, id, classifierContext, cancellationToken).ConfigureAwait(false);
            return CodemodeModelProjection.Result(result);
        }
        if (name is not ("models.getModelsOfType" or "models.getAvailableOfType" or "models.getModelOfType"))
            throw new ArgumentException("Unknown model global.");
        if (input.GetArrayLength() == 0 || input[0].ValueKind != JsonValueKind.String)
            throw new ArgumentException("Model type is required.");
        var type = input[0].GetString()!;
        if (type is not ("chat" or "image" or "classifier")) throw new ArgumentException("Unknown model type.");
        var filter = input.GetArrayLength() < 2 || input[1].ValueKind == JsonValueKind.Null ? null :
            input[1].ValueKind == JsonValueKind.String ? input[1].GetString() : throw new ArgumentException("Provider must be a string.");
        if (name == "models.getAvailableOfType")
            return JsonSerializer.Serialize((await models.GetAvailableAsync(type, filter, cancellationToken).ConfigureAwait(false)).Select(SafeModel), s_json);
        var catalog = models.GetModels(type, filter).Select(SafeModel).ToArray();
        if (name == "models.getModelsOfType") return JsonSerializer.Serialize(catalog, s_json);
        if (filter is null || input.GetArrayLength() != 3 || input[2].ValueKind != JsonValueKind.String)
            throw new ArgumentException("Model lookup expects type, provider and id.");
        var match = catalog.FirstOrDefault(model => model.GetProperty("id").GetString() == input[2].GetString());
        return match.ValueKind == JsonValueKind.Undefined ? null : match.GetRawText();
    }

    private static JsonElement SafeModel(JsonElement model)
    {
        var copy = JsonSerializer.SerializeToNode(model, s_json)!.AsObject();
        copy.Remove("headers");
        return JsonSerializer.SerializeToElement(copy, s_json);
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(property.GetString())
            ? property.GetString()! : throw new ArgumentException($"Model {name} is required.");
}
