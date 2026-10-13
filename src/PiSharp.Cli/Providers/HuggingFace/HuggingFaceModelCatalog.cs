using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads the current Pi Hugging Face provider catalogue snapshot (Pi 6fb2e781).</summary>
internal static class HuggingFaceModelCatalog
{
    private const string ResourceName = "PiSharp.Cli.Providers.Data.huggingface-models.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ModelDescriptor> Load()
    {
        using var stream = typeof(HuggingFaceModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded Hugging Face model catalogue '{ResourceName}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException("Embedded Hugging Face model catalogue is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) || model.Provider != "huggingface" ||
                model.Api != "openai-completions" || model.BaseUrl != "https://router.huggingface.co/v1")
                throw new InvalidDataException("Embedded Hugging Face model catalogue has invalid or duplicate metadata.");
        }
        return Array.AsReadOnly(models);
    }
}
