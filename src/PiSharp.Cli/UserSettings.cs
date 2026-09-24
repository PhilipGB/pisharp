using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Validated pre-prompt compaction defaults; raw session history is never discarded.</summary>
public sealed record CompactionSettings(bool? Enabled = null, int? ReserveTokens = null, int? KeepRecentTokens = null,
    IReadOnlyDictionary<string, CompactionSettings>? ModelOverrides = null)
{
    public static CompactionSettings Parse(JsonElement value, bool overrideEntry = false)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("settings.json compaction must be an object.");
        bool? enabled = null;
        int? reserve = null, keepRecent = null;
        Dictionary<string, CompactionSettings>? modelOverrides = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"settings.json compaction contains duplicate property '{property.Name}'.");
            switch (property.Name)
            {
                case "enabled" when !overrideEntry && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    enabled = property.Value.GetBoolean();
                    break;
                case "reserveTokens" when property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var tokens) && tokens >= 0:
                    reserve = tokens;
                    break;
                case "keepRecentTokens" when property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var recent) && recent >= 0:
                    keepRecent = recent;
                    break;
                case "modelOverrides" when !overrideEntry && property.Value.ValueKind == JsonValueKind.Object:
                    modelOverrides = new Dictionary<string, CompactionSettings>(StringComparer.Ordinal);
                    foreach (var model in property.Value.EnumerateObject())
                    {
                        var separator = model.Name.IndexOf('/');
                        if (separator <= 0 || separator == model.Name.Length - 1 || model.Name.Length > 384 ||
                            !modelOverrides.TryAdd(model.Name, Parse(model.Value, overrideEntry: true)))
                            throw new InvalidDataException("settings.json compaction.modelOverrides contains an invalid or duplicate provider/model ID.");
                    }
                    break;
                default:
                    throw new InvalidDataException($"settings.json compaction.{property.Name} is unsupported or invalid.");
            }
        }
        return new(enabled, reserve, keepRecent, modelOverrides);
    }

    public AutoCompactionPolicy? Resolve(int? contextWindow, Func<string, string?> environment, string? modelKey = null)
    {
        var modelOverride = modelKey is not null && ModelOverrides is not null &&
            ModelOverrides.TryGetValue(modelKey, out var matched) ? matched : null;
        var recent = modelOverride?.KeepRecentTokens ?? KeepRecentTokens;
        var explicitPolicy = AutoCompactionPolicy.FromEnvironment(environment);
        if (explicitPolicy is not null) return explicitPolicy with { KeepRecentTokens = recent };
        if (Enabled == false || contextWindow is null) return null;
        var policy = new AutoCompactionPolicy(contextWindow.Value,
            modelOverride?.ReserveTokens ?? ReserveTokens ?? Math.Min(16_384, contextWindow.Value / 4), recent);
        _ = policy.TriggerTokens;
        return policy;
    }
}
/// <summary>Validated non-secret settings subset for the user and trusted project scopes.</summary>
public sealed record UserSettings(string? DefaultProvider = null, string? DefaultModel = null,
    string? DefaultThinkingLevel = null, IReadOnlyList<string>? DefaultTools = null, string? SessionDirectory = null,
    CompactionSettings? Compaction = null, bool? BlockImages = null, string? DefaultProjectTrust = null, bool? HideThinkingBlock = null)
{
    public static async Task<UserSettings> LoadAsync(string agentDirectory, Func<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        var path = environment("PISHARP_SETTINGS_PATH") ?? Path.Combine(agentDirectory, "settings.json");
        if (!File.Exists(path)) return new();
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024) throw new InvalidDataException("settings.json exceeds 64KB.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("settings.json must contain a JSON object.");
        string? provider = null, model = null, thinking = null, sessionDirectory = null, defaultTrust = null;
        IReadOnlyList<string>? tools = null;
        CompactionSettings? compaction = null;
        bool? blockImages = null, hideThinkingBlock = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException($"settings.json contains duplicate property '{property.Name}'.");
            if (property.Name == "hideThinkingBlock")
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("settings.json hideThinkingBlock must be a boolean.");
                hideThinkingBlock = property.Value.GetBoolean();
                continue;
            }
            if (property.Name == "images")
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("settings.json images must be an object.");
                var imageKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var imageSetting in property.Value.EnumerateObject())
                {
                    if (!imageKeys.Add(imageSetting.Name) || imageSetting.Name != "blockImages" ||
                        imageSetting.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException($"settings.json images.{imageSetting.Name} is unsupported, duplicate or invalid.");
                    blockImages = imageSetting.Value.GetBoolean();
                }
                continue;
            }
            if (property.Name == "compaction")
            {
                compaction = CompactionSettings.Parse(property.Value);
                continue;
            }
            if (property.Name == "defaultTools")
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("settings.json defaultTools must be a string array.");
                var values = new List<string>();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) throw new InvalidDataException("settings.json defaultTools entries must be strings.");
                    var name = item.GetString();
                    if (name is not ("read" or "bash" or "edit" or "write" or "grep" or "find" or "ls") || values.Contains(name, StringComparer.Ordinal))
                        throw new InvalidDataException($"settings.json defaultTools contains an invalid or duplicate tool '{name}'.");
                    values.Add(name);
                }
                tools = values;
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"settings.json property '{property.Name}' must be a string.");
            var value = property.Value.GetString();
            switch (property.Name)
            {
                case "defaultProjectTrust":
                    defaultTrust = Validate(value, property.Name, 8);
                    if (defaultTrust is not ("ask" or "always" or "never"))
                        throw new InvalidDataException("settings.json defaultProjectTrust must be ask, always or never.");
                    break;
                case "defaultProvider": provider = Validate(value, property.Name, 128); break;
                case "defaultModel": model = Validate(value, property.Name, 256); break;
                case "sessionDir": sessionDirectory = Validate(value, property.Name, 1024); break;
                case "defaultThinkingLevel":
                    thinking = Validate(value, property.Name, 16)?.ToLowerInvariant();
                    if (!ThinkingLevels.IsValid(thinking))
                        throw new InvalidDataException("settings.json defaultThinkingLevel is invalid.");
                    break;
                default: throw new InvalidDataException($"settings.json contains unsupported property '{property.Name}'.");
            }
        }
        return new(provider, model, thinking, tools, sessionDirectory, compaction, blockImages, defaultTrust, hideThinkingBlock);
    }

    /// <summary>Overlay a trusted project's explicitly specified defaults; nested compaction values merge.</summary>
    public UserSettings Overlay(UserSettings project) => new(
        project.DefaultProvider ?? DefaultProvider,
        project.DefaultModel ?? DefaultModel,
        project.DefaultThinkingLevel ?? DefaultThinkingLevel,
        project.DefaultTools ?? DefaultTools,
        project.SessionDirectory ?? SessionDirectory,
        project.Compaction is null ? Compaction : new CompactionSettings(
            project.Compaction.Enabled ?? Compaction?.Enabled,
            project.Compaction.ReserveTokens ?? Compaction?.ReserveTokens,
            project.Compaction.KeepRecentTokens ?? Compaction?.KeepRecentTokens,
            MergeOverrides(Compaction?.ModelOverrides, project.Compaction.ModelOverrides)),
        project.BlockImages ?? BlockImages,
        DefaultProjectTrust,
        project.HideThinkingBlock ?? HideThinkingBlock);

    private static IReadOnlyDictionary<string, CompactionSettings>? MergeOverrides(
        IReadOnlyDictionary<string, CompactionSettings>? global, IReadOnlyDictionary<string, CompactionSettings>? project)
    {
        if (project is null) return global;
        var merged = global is null ? new Dictionary<string, CompactionSettings>(StringComparer.Ordinal) :
            new Dictionary<string, CompactionSettings>(global, StringComparer.Ordinal);
        foreach (var (model, setting) in project)
        {
            merged.TryGetValue(model, out var existing);
            merged[model] = new CompactionSettings(ReserveTokens: setting.ReserveTokens ?? existing?.ReserveTokens,
                KeepRecentTokens: setting.KeepRecentTokens ?? existing?.KeepRecentTokens);
        }
        return merged;
    }
    public static async Task<UserSettings> LoadProjectAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(workingDirectory, _ => Path.Combine(workingDirectory, ".pi", "settings.json"), cancellationToken);
        if (settings.DefaultProjectTrust is not null)
            throw new InvalidDataException("defaultProjectTrust is only allowed in user settings.json.");
        return settings;
    }
    public AutoCompactionPolicy? ResolveCompaction(int? contextWindow, Func<string, string?> environment, string? modelKey = null) =>
        (Compaction ?? new CompactionSettings()).Resolve(contextWindow, environment, modelKey);
    public CliArguments ApplyDefaults(CliArguments cli, Func<string, string?> environment, bool preserveSessionModel = false)
    {
        var useLocal = cli.Local || (!preserveSessionModel && cli.Provider is null && DefaultProvider == "local");
        var provider = useLocal ? null : cli.Provider ?? (preserveSessionModel ? null : DefaultProvider);
        var modelEnvironment = provider?.ToLowerInvariant() switch
        {
            "openrouter" => "PISHARP_OPENROUTER_MODEL",
            "mistral" => "PISHARP_MISTRAL_MODEL",
            _ => "PISHARP_MODEL"
        };
        return cli with
        {
            Provider = provider,
            Local = useLocal,
            ModelOverride = cli.ModelOverride ?? (!useLocal && !preserveSessionModel && environment(modelEnvironment) is null ? DefaultModel : null),
            Thinking = cli.Thinking ?? DefaultThinkingLevel,
            Tools = cli.Tools ?? (!cli.NoTools ? DefaultTools : null)
        };
    }

    private static string? Validate(string? value, string property, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value != value.Trim())
            throw new InvalidDataException($"settings.json {property} must be a nonempty string of at most {maxLength} characters.");
        return value;
    }
}
