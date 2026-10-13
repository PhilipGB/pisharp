using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads the checked-in Amazon Bedrock chat catalogue generated from current Pi.</summary>
internal static class BedrockModelCatalog
{
    private const string ResourceName = "PiSharp.Cli.Providers.Data.amazon-bedrock-models.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ModelDescriptor> Load()
    {
        using var stream = typeof(BedrockModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded Bedrock model catalogue '{ResourceName}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException("Embedded Bedrock model catalogue is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) ||
                model.Provider != "amazon-bedrock" || model.Api != "bedrock-converse-stream" ||
                model.BaseUrl is null || !BedrockProviderOptions.IsStandardEndpoint(model.BaseUrl))
                throw new InvalidDataException("Embedded Bedrock model catalogue has invalid or duplicate model metadata.");
        }
        if (models.Length != 174)
            throw new InvalidDataException($"Embedded Bedrock model catalogue has {models.Length} entries; expected 174 from its pinned Pi snapshot.");
        return Array.AsReadOnly(models);
    }
}
