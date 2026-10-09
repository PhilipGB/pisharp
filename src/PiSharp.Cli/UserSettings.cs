using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Validated pre-prompt compaction defaults; raw session history is never discarded.</summary>
public sealed record CompactionSettings(bool? Enabled = null, int? ReserveTokens = null, int? KeepRecentTokens = null,
    IReadOnlyDictionary<string, CompactionSettings>? ModelOverrides = null)
{
    public const int DefaultReserveTokens = AutoCompactionPolicy.DefaultReserveTokens;
    public const int DefaultKeepRecentTokens = 20_000;

    public int ResolveKeepRecentTokens(string? modelKey = null)
    {
        var modelOverride = modelKey is not null && ModelOverrides is not null &&
            ModelOverrides.TryGetValue(modelKey, out var matched) ? matched : null;
        return modelOverride?.KeepRecentTokens ?? KeepRecentTokens ?? DefaultKeepRecentTokens;
    }

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
        => ResolveCore(contextWindow, environment, modelKey, honorEnabledSetting: true);

    public AutoCompactionPolicy? ResolvePolicy(int? contextWindow, Func<string, string?> environment, string? modelKey = null)
        => ResolveCore(contextWindow, environment, modelKey, honorEnabledSetting: false);

    private AutoCompactionPolicy? ResolveCore(int? contextWindow, Func<string, string?> environment, string? modelKey,
        bool honorEnabledSetting)
    {
        var modelOverride = modelKey is not null && ModelOverrides is not null &&
            ModelOverrides.TryGetValue(modelKey, out var matched) ? matched : null;
        var recent = ResolveKeepRecentTokens(modelKey);
        var explicitPolicy = AutoCompactionPolicy.FromEnvironment(environment);
        if (explicitPolicy is not null) return explicitPolicy with { KeepRecentTokens = recent };
        if (honorEnabledSetting && Enabled == false || contextWindow is null) return null;
        var policy = new AutoCompactionPolicy(contextWindow.Value,
            modelOverride?.ReserveTokens ?? ReserveTokens ?? DefaultReserveTokens, recent);
        _ = policy.TriggerTokens;
        return policy;
    }
}
public sealed record UserSettings(string? DefaultProvider = null, string? DefaultModel = null,
    string? DefaultThinkingLevel = null, IReadOnlyList<string>? DefaultTools = null, string? SessionDirectory = null,
    CompactionSettings? Compaction = null, bool? BlockImages = null, string? DefaultProjectTrust = null, bool? HideThinkingBlock = null, QuietStartupMode? QuietStartup = null, IReadOnlyList<string>? EnabledModels = null, string? ShellPath = null, string? ExternalEditor = null, string? Theme = null,
    RetrySettings? Retry = null, PromptDeliveryMode? SteeringMode = null, PromptDeliveryMode? FollowUpMode = null,
    IReadOnlyDictionary<string, string>? ModelThinkingLevels = null, string? HttpProxy = null,
    int? HttpIdleTimeoutMs = null, string? MarkdownCodeBlockIndent = null, string? TerminalTrueColor = null,
    bool? EnableSkillCommands = null, IReadOnlyList<string>? Extensions = null,
    IReadOnlyList<string>? Skills = null, IReadOnlyList<string>? Prompts = null,
    IReadOnlyList<string>? Themes = null, string? ShellCommandPrefix = null)
{
    public const int DefaultHttpIdleTimeoutMs = 300_000;
    public const string DefaultMarkdownCodeBlockIndent = "  ";

    public bool? TerminalTrueColorOverride => TerminalTrueColor switch
    {
        "true" => true,
        "false" => false,
        _ => null
    };

    public bool SkillCommandsEnabled => EnableSkillCommands ?? true;

    public static async Task<UserSettings> LoadAsync(string agentDirectory, Func<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        var path = GetSettingsPath(agentDirectory, environment);
        if (!File.Exists(path)) return new();
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024) throw new InvalidDataException("settings.json exceeds 64KB.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("settings.json must contain a JSON object.");
        string? provider = null, model = null, thinking = null, sessionDirectory = null, defaultTrust = null, shellPath = null,
            externalEditor = null, theme = null;
        IReadOnlyList<string>? tools = null, enabledModels = null;
        IReadOnlyList<string>? extensions = null, skills = null, prompts = null, themes = null;
        IReadOnlyDictionary<string, string>? modelThinkingLevels = null;
        CompactionSettings? compaction = null;
        RetrySettings? retry = null;
        PromptDeliveryMode? steeringMode = null, followUpMode = null;
        bool? blockImages = null, hideThinkingBlock = null, enableSkillCommands = null;
        QuietStartupMode? quietStartup = null;
        string? httpProxy = null;
        int? httpIdleTimeoutMs = null;
        string? markdownCodeBlockIndent = null;
        string? terminalTrueColor = null;
        string? shellCommandPrefix = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException($"settings.json contains duplicate property '{property.Name}'.");
            if (property.Name == "httpIdleTimeoutMs")
            {
                httpIdleTimeoutMs = ParseIdleTimeout(property.Value);
                continue;
            }
            if (property.Name == "quietStartup")
            {
                quietStartup = QuietStartupModes.Parse(property.Value);
                continue;
            }
            if (property.Name == "hideThinkingBlock")
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("settings.json hideThinkingBlock must be a boolean.");
                hideThinkingBlock = property.Value.GetBoolean();
                continue;
            }
            if (property.Name == "enableSkillCommands")
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("settings.json enableSkillCommands must be a boolean.");
                enableSkillCommands = property.Value.GetBoolean();
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
            if (property.Name == "markdown")
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("settings.json markdown must be an object.");
                var markdownKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var markdownSetting in property.Value.EnumerateObject())
                {
                    if (!markdownKeys.Add(markdownSetting.Name) || markdownSetting.Name != "codeBlockIndent" ||
                        markdownSetting.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException($"settings.json markdown.{markdownSetting.Name} is unsupported, duplicate or invalid.");
                    markdownCodeBlockIndent = markdownSetting.Value.GetString();
                }
                continue;
            }
            if (property.Name == "terminal")
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("settings.json terminal must be an object.");
                var terminalKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var terminalSetting in property.Value.EnumerateObject())
                {
                    if (!terminalKeys.Add(terminalSetting.Name) || terminalSetting.Name != "trueColor")
                        throw new InvalidDataException($"settings.json terminal.{terminalSetting.Name} is unsupported or duplicated.");
                    terminalTrueColor = terminalSetting.Value.ValueKind switch
                    {
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.String when terminalSetting.Value.GetString() == "auto" => "auto",
                        _ => throw new InvalidDataException("settings.json terminal.trueColor must be a boolean or 'auto'.")
                    };
                }
                continue;
            }
            if (property.Name == "compaction")
            {
                compaction = CompactionSettings.Parse(property.Value);
                continue;
            }
            if (property.Name == "retry")
            {
                retry = RetrySettings.Parse(property.Value);
                continue;
            }
            if (property.Name == "enabledModels")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 128)
                    throw new InvalidDataException("settings.json enabledModels must be an array of at most 128 patterns.");
                var patterns = new List<string>();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } pattern ||
                        string.IsNullOrWhiteSpace(pattern) || pattern.Length > 512 || pattern != pattern.Trim() ||
                        patterns.Contains(pattern, StringComparer.Ordinal))
                        throw new InvalidDataException("settings.json enabledModels contains an invalid or duplicate pattern.");
                    patterns.Add(pattern);
                }
                enabledModels = patterns;
                continue;
            }
            if (property.Name is "extensions" or "skills" or "prompts" or "themes")
            {
                var parsed = ParseResourcePaths(property.Value, property.Name);
                switch (property.Name)
                {
                    case "extensions": extensions = parsed; break;
                    case "skills": skills = parsed; break;
                    case "prompts": prompts = parsed; break;
                    case "themes": themes = parsed; break;
                }
                continue;
            }
            if (property.Name == "modelThinkingLevels")
            {
                modelThinkingLevels = ParseModelThinkingLevels(property.Value);
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
                    values.Add(item.GetString()!);
                }
                tools = values;
                continue;
            }
            if (property.Name == "defaultProjectTrust")
            {
                var candidate = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                defaultTrust = candidate is "ask" or "always" or "never" ? candidate : null;
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"settings.json property '{property.Name}' must be a string.");
            var value = property.Value.GetString();
            switch (property.Name)
            {
                case "shellCommandPrefix": shellCommandPrefix = value; break;
                case "defaultProvider": provider = Validate(value, property.Name, 128); break;
                case "defaultModel": model = Validate(value, property.Name, 256); break;
                case "sessionDir": sessionDirectory = Validate(value, property.Name, 1024); break;
                case "shellPath": shellPath = Validate(value, property.Name, 1024); break;
                case "externalEditor": externalEditor = Validate(value, property.Name, 4096); break;
                case "httpProxy":
                    httpProxy = ValidateHttpProxy(Validate(value, property.Name, 2048));
                    break;
                case "theme":
                    theme = Validate(value, property.Name, 128);
                    if (theme is null || !IsValidThemeSetting(theme))
                        throw new InvalidDataException("settings.json theme must be a theme name or a light/dark theme pair.");
                    break;
                case "defaultThinkingLevel":
                    thinking = Validate(value, property.Name, 16)?.ToLowerInvariant();
                    if (!ThinkingLevels.IsValid(thinking))
                        throw new InvalidDataException("settings.json defaultThinkingLevel is invalid.");
                    break;
                case "steeringMode":
                    if (!PromptDeliveryModes.TryParseSettingValue(value, out var parsedSteeringMode))
                        throw new InvalidDataException("settings.json steeringMode must be all or one-at-a-time.");
                    steeringMode = parsedSteeringMode;
                    break;
                case "followUpMode":
                    if (!PromptDeliveryModes.TryParseSettingValue(value, out var parsedFollowUpMode))
                        throw new InvalidDataException("settings.json followUpMode must be all or one-at-a-time.");
                    followUpMode = parsedFollowUpMode;
                    break;
                default: throw new InvalidDataException($"settings.json contains unsupported property '{property.Name}'.");
            }
        }
        return new(provider, model, thinking, tools, sessionDirectory, compaction, blockImages, defaultTrust, hideThinkingBlock,
            quietStartup, enabledModels, shellPath, externalEditor, theme, retry, steeringMode, followUpMode,
            modelThinkingLevels, httpProxy, httpIdleTimeoutMs, markdownCodeBlockIndent, terminalTrueColor,
            enableSkillCommands, extensions, skills, prompts, themes, shellCommandPrefix);
    }

    public static string GetSettingsPath(string agentDirectory, Func<string, string?> environment) =>
        Path.GetFullPath(environment("PISHARP_SETTINGS_PATH") ?? Path.Combine(agentDirectory, "settings.json"));

    /// <summary>Overlay a trusted project's explicitly specified defaults; nested compaction values merge.</summary>
    public UserSettings Overlay(UserSettings project) => new(
        project.DefaultProvider ?? DefaultProvider,
        project.DefaultModel ?? DefaultModel,
        project.DefaultThinkingLevel ?? DefaultThinkingLevel,
        MergeDefaultTools(DefaultTools, project.DefaultTools),
        project.SessionDirectory ?? SessionDirectory,
        project.Compaction is null ? Compaction : new CompactionSettings(
            project.Compaction.Enabled ?? Compaction?.Enabled,
            project.Compaction.ReserveTokens ?? Compaction?.ReserveTokens,
            project.Compaction.KeepRecentTokens ?? Compaction?.KeepRecentTokens,
            MergeOverrides(Compaction?.ModelOverrides, project.Compaction.ModelOverrides)),
        project.BlockImages ?? BlockImages,
        DefaultProjectTrust,
        project.HideThinkingBlock ?? HideThinkingBlock,
        project.QuietStartup ?? QuietStartup,
        project.EnabledModels ?? EnabledModels,
        project.ShellPath ?? ShellPath,
        project.ExternalEditor ?? ExternalEditor,
        project.Theme ?? Theme,
        RetrySettings.Merge(Retry, project.Retry),
        project.SteeringMode ?? SteeringMode,
        project.FollowUpMode ?? FollowUpMode,
        MergeModelThinkingLevels(ModelThinkingLevels, project.ModelThinkingLevels), HttpProxy,
        project.HttpIdleTimeoutMs ?? HttpIdleTimeoutMs,
        project.MarkdownCodeBlockIndent ?? MarkdownCodeBlockIndent,
        project.TerminalTrueColor ?? TerminalTrueColor,
        project.EnableSkillCommands ?? EnableSkillCommands,
        MergeResourcePaths(Extensions, project.Extensions),
        MergeResourcePaths(Skills, project.Skills),
        MergeResourcePaths(Prompts, project.Prompts),
        MergeResourcePaths(Themes, project.Themes),
        project.ShellCommandPrefix ?? ShellCommandPrefix);

    public string? GetModelThinkingLevel(string provider, string modelId) =>
        ModelThinkingLevels?.GetValueOrDefault($"{provider}/{modelId}");

    public string? GetModelThinkingLevel(ModelSelection selection) =>
        GetModelThinkingLevel(selection.Provider.Id, selection.Model.Id);

    /// <summary>Apply Pi's user-wide proxy default without overriding explicit process configuration.</summary>
    public void ApplyHttpProxyEnvironment(Func<string, string?> getEnvironment, Action<string, string?> setEnvironment)
    {
        if (HttpProxy is null) return;
        if (getEnvironment("HTTP_PROXY") is null) setEnvironment("HTTP_PROXY", HttpProxy);
        if (getEnvironment("HTTPS_PROXY") is null) setEnvironment("HTTPS_PROXY", HttpProxy);
    }

    private static bool IsValidThemeSetting(string value)
    {
        if (value.Length is 0 or > 128 || value.Any(char.IsControl)) return false;
        var separator = value.IndexOf('/');
        if (separator < 0) return true;
        if (value.IndexOf('/', separator + 1) >= 0) return false;
        return !string.IsNullOrWhiteSpace(value[..separator]) && !string.IsNullOrWhiteSpace(value[(separator + 1)..]);
    }

    private static IReadOnlyList<string>? ParseResourcePaths(JsonElement value, string setting)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 512)
            throw new InvalidDataException($"settings.json {setting} must be an array of at most 512 paths or patterns.");
        var entries = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } entry ||
                entry.Length is 0 or > 4096 || entry != entry.Trim() || entry.Any(char.IsControl) ||
                entry is "!" or "+" or "-")
                throw new InvalidDataException($"settings.json {setting} contains an invalid path or pattern.");
            entries.Add(entry);
        }
        return entries;
    }

    private static IReadOnlyList<string>? MergeResourcePaths(IReadOnlyList<string>? user,
        IReadOnlyList<string>? project) => user is null ? project : project is null ? user : [.. user, .. project];

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

    private static IReadOnlyDictionary<string, string>? ParseModelThinkingLevels(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() > 512)
            throw new InvalidDataException("settings.json modelThinkingLevels must be an object of at most 512 entries.");
        var levels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var separator = property.Name.IndexOf('/');
            if (separator <= 0 || separator == property.Name.Length - 1 || property.Name.Length > 512 ||
                property.Value.ValueKind != JsonValueKind.String ||
                !ThinkingLevels.IsValid(property.Value.GetString()) ||
                !levels.TryAdd(property.Name, property.Value.GetString()!.ToLowerInvariant()))
                throw new InvalidDataException("settings.json modelThinkingLevels contains an invalid or duplicate provider/model entry.");
        }
        return levels;
    }

    private static IReadOnlyDictionary<string, string>? MergeModelThinkingLevels(
        IReadOnlyDictionary<string, string>? global, IReadOnlyDictionary<string, string>? project)
    {
        if (project is null) return global;
        var merged = global is null ? new Dictionary<string, string>(StringComparer.Ordinal) :
            new Dictionary<string, string>(global, StringComparer.Ordinal);
        foreach (var (model, level) in project) merged[model] = level;
        return merged;
    }

    public static async Task<UserSettings> LoadProjectAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(workingDirectory, _ => Path.Combine(workingDirectory, ".pi", "settings.json"), cancellationToken);
        if (settings.HttpProxy is not null)
            throw new InvalidDataException("httpProxy is only allowed in user settings.json.");
        return settings;
    }
    public AutoCompactionPolicy? ResolveCompaction(int? contextWindow, Func<string, string?> environment, string? modelKey = null) =>
        (Compaction ?? new CompactionSettings()).Resolve(contextWindow, environment, modelKey);
    public AutoCompactionPolicy? ResolveCompactionPolicy(int? contextWindow, Func<string, string?> environment,
        string? modelKey = null) =>
        (Compaction ?? new CompactionSettings()).ResolvePolicy(contextWindow, environment, modelKey);
    public int ResolveCompactionKeepRecentTokens(string? modelKey = null) =>
        (Compaction ?? new CompactionSettings()).ResolveKeepRecentTokens(modelKey);
    public bool AutoCompactionEnabled(Func<string, string?> environment) =>
        (Compaction ?? new CompactionSettings()).Enabled != false ||
        environment("PISHARP_CONTEXT_WINDOW_TOKENS") is not null;
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
            ScopedModels = cli.ScopedModels ?? EnabledModels,
            Tools = cli.Tools ?? (!cli.NoTools ? ResolveDefaultTools(DefaultTools) : null)
        };
    }

    private static bool IsToolModifier(string entry) =>
        entry.StartsWith('+') || entry.StartsWith('-');

    private static IReadOnlyList<string>? MergeDefaultTools(IReadOnlyList<string>? inherited,
        IReadOnlyList<string>? overrides)
    {
        if (overrides is null) return inherited;
        return inherited is not null && overrides.All(IsToolModifier)
            ? inherited.Concat(overrides).ToArray() : overrides;
    }

    private static IReadOnlyList<string>? ResolveDefaultTools(IReadOnlyList<string>? entries)
    {
        if (entries is null) return null;
        var plain = entries.Where(entry => !IsToolModifier(entry)).ToArray();
        var tools = plain.Length > 0 || entries.Count == 0
            ? plain.ToList() : new List<string> { "read", "bash", "edit", "write" };
        foreach (var entry in entries.Where(IsToolModifier))
        {
            var name = entry[1..];
            var index = tools.IndexOf(name);
            if (entry[0] == '+' && index < 0 && name.Length > 0) tools.Add(name);
            else if (entry[0] == '-' && index >= 0) tools.RemoveAt(index);
        }
        return tools;
    }

    private static string? Validate(string? value, string property, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value != value.Trim())
            throw new InvalidDataException($"settings.json {property} must be a nonempty string of at most {maxLength} characters.");
        return value;
    }

    private static int ParseIdleTimeout(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (string.Equals(text, "disabled", StringComparison.OrdinalIgnoreCase)) return 0;
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedStringTimeout))
                return NormalizeIdleTimeout(parsedStringTimeout);
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var milliseconds))
            return NormalizeIdleTimeout(milliseconds);
        throw new InvalidDataException("settings.json httpIdleTimeoutMs must be a nonnegative number or 'disabled'.");
    }

    private static int NormalizeIdleTimeout(double milliseconds)
    {
        if (double.IsFinite(milliseconds) && milliseconds is >= 0 and <= int.MaxValue)
            return (int)Math.Floor(milliseconds);
        throw new InvalidDataException("settings.json httpIdleTimeoutMs must be a nonnegative number or 'disabled'.");
    }

    private static string ValidateHttpProxy(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var proxy) ||
            proxy.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(proxy.Host) ||
            proxy.Query.Length > 0 || proxy.Fragment.Length > 0)
            throw new InvalidDataException("settings.json httpProxy must be an absolute HTTP or HTTPS proxy URL without a query or fragment.");
        return value!;
    }
}
