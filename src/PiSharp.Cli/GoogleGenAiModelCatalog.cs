using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads the checked-in Google GenAI chat catalog generated from current Pi main.</summary>
internal static class GoogleGenAiModelCatalog
{
    private const string ResourceName = "PiSharp.Cli.Providers.Data.google-generative-ai-models.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ModelDescriptor> Load()
    {
        using var stream = typeof(GoogleGenAiModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded Google GenAI model catalog '{ResourceName}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException("Embedded Google GenAI model catalog is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) ||
                model.Provider != "google" || model.Api != "google-generative-ai" ||
                model.BaseUrl != "https://generativelanguage.googleapis.com/v1beta")
                throw new InvalidDataException("Embedded Google GenAI model catalog has invalid or duplicate model metadata.");
        return Array.AsReadOnly(models);
    }
}
