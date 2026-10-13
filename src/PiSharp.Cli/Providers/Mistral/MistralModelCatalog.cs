using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads the checked-in Mistral catalogue snapshot from current Pi metadata.</summary>
internal static class MistralModelCatalog
{
    private const string ResourceName = "PiSharp.Cli.Providers.Data.mistral-models.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ModelDescriptor> Load()
    {
        using var stream = typeof(MistralModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded Mistral model catalogue '{ResourceName}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException("Embedded Mistral model catalogue is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) ||
                model.Provider != "mistral" || model.Api != "mistral-conversations")
                throw new InvalidDataException("Embedded Mistral model catalogue has invalid or duplicate model metadata.");
        }
        return Array.AsReadOnly(models);
    }
}
