using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads Pi's current OpenAI Codex chat-model metadata snapshot.</summary>
internal static class OpenAiCodexModelCatalog
{
    private const string ResourceName = "PiSharp.Cli.Providers.Data.openai-codex-responses-models.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<ModelDescriptor> Load()
    {
        using var stream = typeof(OpenAiCodexModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException($"Embedded OpenAI Codex model catalogue '{ResourceName}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException("Embedded OpenAI Codex model catalogue is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) ||
                model.Provider != "openai-codex" || model.Api != "openai-codex-responses" ||
                model.BaseUrl != "https://chatgpt.com/backend-api")
                throw new InvalidDataException("Embedded OpenAI Codex model catalogue has invalid or duplicate model metadata.");
        return Array.AsReadOnly(models);
    }
}
