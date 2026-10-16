using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads the checked-in Azure Foundry Chat Completions model snapshot from current Pi.</summary>
internal static class AzureFoundryModelCatalog
{
    private const string ResourceName = "PiSharp.Cli.Providers.Data.azure-foundry-chat-completions-models.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ModelDescriptor> Load()
    {
        using var stream = typeof(AzureFoundryModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded Azure Foundry model catalogue '{ResourceName}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException("Embedded Azure Foundry model catalogue is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) ||
                model.Provider != "azure" || model.Api != "openai-completions")
                throw new InvalidDataException("Embedded Azure Foundry model catalogue has invalid or duplicate model metadata.");
        return Array.AsReadOnly(models);
    }
}
