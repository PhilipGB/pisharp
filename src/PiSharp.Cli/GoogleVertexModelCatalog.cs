using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads the checked-in Google Vertex Gemini catalog generated from current Pi main.</summary>
internal static class GoogleVertexModelCatalog
{
    private const string ResourceName = "PiSharp.Cli.Providers.Data.google-vertex-models.json";
    private const string ModelBaseUrl = "https://{location}-aiplatform.googleapis.com";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ModelDescriptor> Load()
    {
        using var stream = typeof(GoogleVertexModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded Google Vertex model catalog '{ResourceName}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException("Embedded Google Vertex model catalog is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) ||
                model.Provider != "google-vertex" || model.Api != "google-vertex" ||
                model.BaseUrl != ModelBaseUrl)
                throw new InvalidDataException("Embedded Google Vertex model catalog has invalid or duplicate model metadata.");
        return Array.AsReadOnly(models);
    }
}
