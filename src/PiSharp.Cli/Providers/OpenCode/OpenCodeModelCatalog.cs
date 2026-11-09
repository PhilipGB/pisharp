using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Loads the checked-in OpenCode Zen and OpenCode Go chat catalogues generated from pinned Pi.</summary>
internal static class OpenCodeModelCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> Protocols =
        new(StringComparer.Ordinal)
        {
            "anthropic-messages", "google-generative-ai", "openai-completions", "openai-responses"
        };

    public static IReadOnlyList<ModelDescriptor> Load(string provider)
    {
        var resource = provider switch
        {
            "opencode" => "PiSharp.Cli.Providers.Data.opencode-models.json",
            "opencode-go" => "PiSharp.Cli.Providers.Data.opencode-go-models.json",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider,
                "OpenCode catalogues exist only for the built-in opencode providers.")
        };
        using var stream = typeof(OpenCodeModelCatalog).Assembly.GetManifestResourceStream(resource) ??
            throw new InvalidOperationException($"Embedded OpenCode model catalogue '{resource}' is missing.");
        var models = JsonSerializer.Deserialize<ModelDescriptor[]>(stream, JsonOptions) ??
            throw new InvalidDataException($"Embedded OpenCode model catalogue '{provider}' is empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models)
            if (string.IsNullOrWhiteSpace(model.Id) || !ids.Add(model.Id) ||
                model.Provider != provider || model.Api is null || !Protocols.Contains(model.Api) ||
                !IsPinnedBaseUrl(provider, model.Api, model.BaseUrl))
                throw new InvalidDataException(
                    $"Embedded OpenCode model catalogue '{provider}' has invalid or duplicate model metadata.");
        return Array.AsReadOnly(models);
    }

    private static bool IsPinnedBaseUrl(string provider, string api, string? baseUrl) =>
        (provider, api) switch
        {
            ("opencode", "anthropic-messages") => baseUrl == "https://opencode.ai/inference",
            ("opencode", _) => baseUrl == "https://opencode.ai/inference/v1",
            ("opencode-go", "anthropic-messages") => baseUrl == "https://opencode.ai/inference/go",
            ("opencode-go", _) => baseUrl == "https://opencode.ai/inference/go/v1",
            _ => false
        };
}
