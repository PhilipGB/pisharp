using System.Text;
using System.Text.Json;

namespace PiSharp.Core.Models;

/// <summary>
/// models.json provider configuration shape (pinned Pi: model-config.ts ProviderConfigSchema).
/// Unknown properties are tolerated, matching TypeBox object semantics.
/// </summary>
public sealed record ModelsJsonProvider
{
    public string? Name { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public string? Api { get; init; }
    public string? OAuth { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public ModelCompatValue? Compat { get; init; }
    public bool? AuthHeader { get; init; }
    public IReadOnlyList<ModelsJsonModel>? Models { get; init; }
    public IReadOnlyDictionary<string, ModelsJsonModelOverride>? ModelOverrides { get; init; }
}

/// <summary>
/// models.json model definition (pinned Pi: model-config.ts ModelDefinitionSchema).
/// </summary>
public sealed record ModelsJsonModel
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Api { get; init; }
    public string? BaseUrl { get; init; }
    public bool? Reasoning { get; init; }
    public IReadOnlyDictionary<string, string?>? ThinkingLevelMap { get; init; }
    public IReadOnlyList<string>? Input { get; init; }
    public ModelCostValue? Cost { get; init; }
    public double? ContextWindow { get; init; }
    public double? MaxTokens { get; init; }
    public IReadOnlyDictionary<string, object?>? SamplingParams { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public ModelCompatValue? Compat { get; init; }
}

/// <summary>
/// models.json model override (pinned Pi: model-config.ts ModelOverrideSchema). All fields
/// optional; defined values win over the underlying model.
/// </summary>
public sealed record ModelsJsonModelOverride
{
    public string? Name { get; init; }
    public bool? Reasoning { get; init; }
    public IReadOnlyDictionary<string, string?>? ThinkingLevelMap { get; init; }
    public IReadOnlyList<string>? Input { get; init; }
    public ModelCostValue? Cost { get; init; }
    public double? ContextWindow { get; init; }
    public double? MaxTokens { get; init; }
    public IReadOnlyDictionary<string, object?>? SamplingParams { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public ModelCompatValue? Compat { get; init; }
}

/// <summary>cost rates as parsed from models.json (all four rates required on definitions).</summary>
public sealed record ModelCostValue
{
    public required double Input { get; init; }
    public required double Output { get; init; }
    public required double CacheRead { get; init; }
    public required double CacheWrite { get; init; }
    public IReadOnlyList<ModelCostTierValue>? Tiers { get; init; }
}

/// <summary>A cost tier as parsed from models.json.</summary>
public sealed record ModelCostTierValue
{
    public required double InputTokensAbove { get; init; }
    public required double Input { get; init; }
    public required double Output { get; init; }
    public required double CacheRead { get; init; }
    public required double CacheWrite { get; init; }
}

/// <summary>
/// A compat object as parsed from models.json: raw string-keyed values preserving every
/// field of the three pinned compat schemas. The runtime maps these to typed compat records.
/// </summary>
public sealed record ModelCompatValue
{
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
}

/// <summary>
/// One immutable load of models.json (pinned Pi: model-config.ts ModelConfig). A failed
/// load never throws: the error is captured and the provider set stays empty, so one
/// broken optional catalogue cannot crash the application.
/// </summary>
public sealed class ModelConfig
{
    private readonly Dictionary<string, ModelsJsonProvider> _providers;
    private readonly string? _error;

    private ModelConfig(Dictionary<string, ModelsJsonProvider> providers, string? error = null)
    {
        _providers = providers;
        _error = error;
    }

    /// <summary>Loads and validates models.json. Missing file yields an empty config.</summary>
    public static async Task<ModelConfig> LoadAsync(string? modelsJsonPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(modelsJsonPath))
        {
            return new ModelConfig(new Dictionary<string, ModelsJsonProvider>());
        }

        var path = Path.GetFullPath(modelsJsonPath);
        string content;
        try
        {
            content = await JsonText.ReadFileAsync(path, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return new ModelConfig(new Dictionary<string, ModelsJsonProvider>());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ModelConfig(
                new Dictionary<string, ModelsJsonProvider>(),
                $"Failed to load models.json: {exception.Message}\n\nFile: {path}");
        }

        JsonElement parsed;
        try
        {
            parsed = JsonDocument.Parse(JsonText.StripJsonComments(JsonText.StripBom(content))).RootElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return new ModelConfig(
                new Dictionary<string, ModelsJsonProvider>(),
                $"Failed to parse models.json: {exception.Message}\n\nFile: {path}");
        }

        var errors = ModelConfigValidator.Validate(parsed);
        if (errors.Count > 0)
        {
            var rendered = new StringBuilder();
            foreach (var (propertyPath, message) in errors)
            {
                rendered.AppendLine($"  - {propertyPath}: {message}");
            }

            return new ModelConfig(
                new Dictionary<string, ModelsJsonProvider>(),
                $"Invalid models.json schema:\n{rendered.ToString().TrimEnd()}\n\nFile: {path}");
        }

        return new ModelConfig(ModelConfigParser.ParseProviders(parsed));
    }

    /// <summary>Returns the provider configuration, if present.</summary>
    public ModelsJsonProvider? GetProvider(string providerId) =>
        _providers.TryGetValue(providerId, out var provider) ? provider : null;

    /// <summary>Returns all configured provider ids.</summary>
    public IReadOnlyList<string> GetProviderIds() => _providers.Keys.ToArray();

    /// <summary>Returns the load/parse/validation error, if any.</summary>
    public string? GetError() => _error;
}
