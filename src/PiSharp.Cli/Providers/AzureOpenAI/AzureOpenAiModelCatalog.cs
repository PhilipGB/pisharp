using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads the checked-in Azure OpenAI model snapshot generated from current Pi metadata.</summary>
internal static class AzureOpenAiModelCatalog
{
    private const string ResourceName = "PiSharp.Cli.Providers.Data.azure-openai-responses-models.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ModelDescriptor> Load()
    {
        using var stream = typeof(AzureOpenAiModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded Azure OpenAI model catalogue '{ResourceName}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException("Embedded Azure OpenAI model catalogue is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) ||
                model.Provider != "azure-openai-responses" || model.Api != "azure-openai-responses")
                throw new InvalidDataException("Embedded Azure OpenAI model catalogue has invalid or duplicate model metadata.");
        return Array.AsReadOnly(models);
    }
}
