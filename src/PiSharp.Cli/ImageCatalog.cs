using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

internal static class ImageCatalog
{
    public static void AddBuiltins(Dictionary<string, ProviderProfile> providers)
    {
        using var stream = typeof(ImageCatalog).Assembly.GetManifestResourceStream("PiSharp.Cli.Providers.Data.image-models.json")!;
        using var document = JsonDocument.Parse(stream);
        foreach (var group in document.RootElement.GetProperty("models").EnumerateArray().GroupBy(model => model.GetProperty("provider").GetString()!))
        {
            if (!providers.TryGetValue(group.Key, out var profile))
                throw new InvalidDataException($"Image catalog references unknown provider '{group.Key}'.");
            providers[group.Key] = profile with { Images = group.Select(model => Parse(group.Key, model, profile.Endpoint)).ToArray() };
        }
    }

    public static ImageModelDescriptor Parse(string provider, JsonElement model, Uri endpoint)
    {
        var id = model.GetProperty("id").GetString()!;
        var source = $"Image model '{provider}/{id}'";
        var input = Modalities(model, "input", ["text"]);
        var output = Modalities(model, "output", ["image"]);
        if (!output.Contains("image")) throw new InvalidDataException($"{source} output must include image.");
        return new(provider, id, model.TryGetProperty("name", out var name) ? name.GetString()! : id,
            model.GetProperty("api").GetString()!,
            model.TryGetProperty("baseUrl", out var url) ? ProviderProfileLoader.ParseEndpoint(url.GetString()!, source) : endpoint,
            input, output, ProviderProfileLoader.ParsePricing(model), ModelInputLimitsParser.Parse(model, source, strict: true));
    }

    private static IReadOnlyList<string> Modalities(JsonElement model, string property, string[] defaults)
    {
        if (!model.TryGetProperty(property, out var values)) return defaults;
        if (values.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Image model {property} must be a modality array.");
        var result = values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null).ToArray();
        if (result.Length == 0 || result.Any(value => value is not ("text" or "image")))
            throw new InvalidDataException($"Image model {property} must contain text/image modalities.");
        return result.Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
    }
}
