using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PiSharp.Core.Settings;

/// <summary>Which settings file a value or diagnostic belongs to.</summary>
public enum SettingsScope
{
    /// <summary>The global user settings file under the agent directory.</summary>
    Global,

    /// <summary>The trusted project settings file (.pi/settings.json).</summary>
    Project,
}

/// <summary>A settings load or write problem, reported as a warning rather than a crash.</summary>
public sealed record SettingsDiagnostic(SettingsScope Scope, string? Path, string Message)
{
    /// <summary>Formats the diagnostic the same way Pi renders it.</summary>
    public string RenderMessage() => Path is null
        ? $"Invalid {Scope.ToString().ToLowerInvariant()} settings: {Message}"
        : $"Invalid settings file {Path}: {Message}";
}

/// <summary>
/// Persistence boundary for settings files. Implementations must be safe for
/// concurrent use within the process; cross-process locking is a documented
/// intentional difference from Pi's proper-lockfile usage.
/// </summary>
public interface ISettingsStorage
{
    /// <summary>Reads the raw settings content for a scope, or null when the file is absent.</summary>
    Task<string?> ReadAsync(SettingsScope scope, CancellationToken cancellationToken = default);

    /// <summary>Writes the raw settings content for a scope, creating parent directories.</summary>
    Task WriteAsync(SettingsScope scope, string content, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes the Pi settings files on disk.</summary>
public sealed class FileSettingsStorage : ISettingsStorage
{
    private readonly string _globalPath;
    private readonly string _projectPath;

    /// <summary>Creates a file-backed store for the given working directory and agent directory.</summary>
    public FileSettingsStorage(string cwd, string agentDir)
    {
        _globalPath = Path.Combine(agentDir, "settings.json");
        _projectPath = Path.Combine(Path.GetFullPath(cwd), ".pi", "settings.json");
    }

    /// <summary>Gets the global settings file path.</summary>
    public string GlobalPath => _globalPath;

    /// <summary>Gets the project settings file path.</summary>
    public string ProjectPath => _projectPath;

    /// <inheritdoc/>
    public async Task<string?> ReadAsync(SettingsScope scope, CancellationToken cancellationToken = default)
    {
        var path = scope == SettingsScope.Global ? _globalPath : _projectPath;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SettingsReadException($"Unable to read settings file {path}: {exception.Message}", exception);
        }
    }

    /// <inheritdoc/>
    public async Task WriteAsync(SettingsScope scope, string content, CancellationToken cancellationToken = default)
    {
        var path = scope == SettingsScope.Global ? _globalPath : _projectPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }
}

/// <summary>Raised when a settings file cannot be read; converted to a diagnostic by the manager.</summary>
public sealed class SettingsReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>In-memory settings store for deterministic tests.</summary>
public sealed class InMemorySettingsStorage : ISettingsStorage
{
    private readonly object _sync = new();
    private string? _global;
    private string? _project;

    /// <summary>Creates an empty in-memory store.</summary>
    public InMemorySettingsStorage()
    {
    }

    /// <summary>Creates an in-memory store with initial file contents.</summary>
    public InMemorySettingsStorage(string? global, string? project)
    {
        _global = global;
        _project = project;
    }

    /// <inheritdoc/>
    public Task<string?> ReadAsync(SettingsScope scope, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(scope == SettingsScope.Global ? _global : _project);
        }
    }

    /// <inheritdoc/>
    public Task WriteAsync(SettingsScope scope, string content, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (scope == SettingsScope.Global)
            {
                _global = content;
            }
            else
            {
                _project = content;
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Application-level Pi settings: loads the global agent settings and the trusted project
/// settings, merges them with Pi's deep-merge rules, exposes typed accessors with Pi
/// defaults, and persists changes while preserving fields edited externally. Mirrors the
/// pinned Pi <c>SettingsManager</c> semantics (migrations, trust gating, diagnostics,
/// modified-field save behaviour).
/// </summary>
public sealed partial class SettingsManager
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        // Absent (null) typed properties must serialize away so typed merges treat them as
        // "not set"; explicit JSON nulls in files remain in the raw JsonObject stores.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new PackageSourceJsonConverter() },
        };
        return options;
    }

    private readonly ISettingsStorage _storage;
    private readonly IReadOnlyDictionary<SettingsScope, string> _paths;
    private readonly object _queueLock = new();
    private Task _writeQueue = Task.CompletedTask;

    private JsonObject _globalRaw = new();
    private JsonObject _projectRaw = new();
    private PiSettings _global = new();
    private PiSettings _project = new();
    private PiSettings _effective = new();
    private bool _projectTrusted;
    private bool _globalLoadError;
    private bool _projectLoadError;
    private readonly HashSet<string> _modifiedGlobal = new();
    private readonly Dictionary<string, HashSet<string>> _modifiedGlobalNested = new();
    private readonly HashSet<string> _modifiedProject = new();
    private readonly Dictionary<string, HashSet<string>> _modifiedProjectNested = new();
    private readonly List<SettingsDiagnostic> _diagnostics = new();

    private SettingsManager(
        ISettingsStorage storage,
        JsonObject globalRaw,
        JsonObject projectRaw,
        bool projectTrusted,
        IReadOnlyDictionary<SettingsScope, string> paths,
        SettingsDiagnostic? globalError,
        SettingsDiagnostic? projectError)
    {
        _storage = storage;
        _paths = paths;
        _globalRaw = globalRaw;
        _projectRaw = projectRaw;
        _projectTrusted = projectTrusted;
        _globalLoadError = globalError is not null;
        _projectLoadError = projectError is not null;
        _global = DeserializeSettings(globalRaw);
        _project = DeserializeSettings(projectRaw);
        _effective = MergeSettings(_global, _project);
        if (globalError is not null)
        {
            _diagnostics.Add(globalError);
        }
        if (projectError is not null)
        {
            _diagnostics.Add(projectError);
        }
    }

    /// <summary>
    /// Creates a settings manager from the Pi settings files for a working directory.
    /// Project settings are only read while the project is trusted.
    /// </summary>
    public static async Task<SettingsManager> CreateAsync(
        string cwd,
        bool projectTrusted = true,
        string? homeDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var agentDir = SettingsPaths.GetAgentDir(homeDirectory);
        var storage = new FileSettingsStorage(cwd, agentDir);
        var paths = new Dictionary<SettingsScope, string>
        {
            [SettingsScope.Global] = storage.GlobalPath,
            [SettingsScope.Project] = storage.ProjectPath,
        };
        return await CreateFromStorageAsync(storage, projectTrusted, paths, cancellationToken);
    }

    /// <summary>Creates a settings manager over an arbitrary storage backend.</summary>
    public static async Task<SettingsManager> CreateFromStorageAsync(
        ISettingsStorage storage,
        bool projectTrusted = true,
        IReadOnlyDictionary<SettingsScope, string>? paths = null,
        CancellationToken cancellationToken = default)
    {
        var global = await LoadScopeAsync(storage, SettingsScope.Global, paths, cancellationToken);
        (JsonObject Value, SettingsDiagnostic? Error) project;
        if (projectTrusted)
        {
            project = await LoadScopeAsync(storage, SettingsScope.Project, paths, cancellationToken);
        }
        else
        {
            project = (new JsonObject(), null);
        }
        return new SettingsManager(
            storage,
            global.Value,
            project.Value,
            projectTrusted,
            paths ?? EmptyPaths,
            global.Error,
            project.Error);
    }

    private static readonly IReadOnlyDictionary<SettingsScope, string> EmptyPaths =
        new Dictionary<SettingsScope, string>();

    private static async Task<(JsonObject Value, SettingsDiagnostic? Error)> LoadScopeAsync(
        ISettingsStorage storage,
        SettingsScope scope,
        IReadOnlyDictionary<SettingsScope, string>? paths,
        CancellationToken cancellationToken)
    {
        string? path = paths is null ? null : paths.GetValueOrDefault(scope);
        string? content;
        try
        {
            content = await storage.ReadAsync(scope, cancellationToken);
        }
        catch (SettingsReadException exception)
        {
            return (new JsonObject(), new SettingsDiagnostic(scope, path, exception.Message));
        }

        if (string.IsNullOrEmpty(content))
        {
            return (new JsonObject(), null);
        }

        return ParseSettingsContent(content, scope, path);
    }

    private static (JsonObject Value, SettingsDiagnostic? Error) ParseSettingsContent(
        string content,
        SettingsScope scope,
        string? path)
    {
        try
        {
            using var document = JsonDocument.Parse(content.TrimStart('\uFEFF'));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (new JsonObject(), new SettingsDiagnostic(scope, path, "Settings root must be a JSON object."));
            }

            var settings = document.RootElement.Deserialize<JsonObject>(SerializerOptions) ?? new JsonObject();
            MigrateSettings(settings);
            return (settings, null);
        }
        catch (JsonException exception)
        {
            return (new JsonObject(), new SettingsDiagnostic(scope, path, exception.Message));
        }
    }

    /// <summary>Gets whether the project scope is currently trusted.</summary>
    public bool IsProjectTrusted => _projectTrusted;

    /// <summary>Gets the resolved settings file path for each scope.</summary>
    public IReadOnlyDictionary<SettingsScope, string> ScopePaths => _paths;

    /// <summary>
    /// Updates the trust gate. Untrusting drops the project scope in memory and clears its
    /// pending writes; trusting re-reads the project file from storage (Pi semantics).
    /// </summary>
    public async Task SetProjectTrustedAsync(bool trusted, CancellationToken cancellationToken = default)
    {
        if (_projectTrusted == trusted)
        {
            return;
        }

        _projectTrusted = trusted;
        _modifiedProject.Clear();
        foreach (var nested in _modifiedProjectNested.Values)
        {
            nested.Clear();
        }

        if (!trusted)
        {
            _projectRaw = new JsonObject();
            _project = new PiSettings();
            _projectLoadError = false;
            _effective = MergeSettings(_global, _project);
            return;
        }

        var load = await LoadScopeAsync(_storage, SettingsScope.Project, _paths, cancellationToken);
        _projectRaw = load.Value;
        _project = DeserializeSettings(_projectRaw);
        _projectLoadError = load.Error is not null;
        if (load.Error is not null)
        {
            _diagnostics.Add(load.Error);
        }
        _effective = MergeSettings(_global, _project);
    }

    /// <summary>
    /// Re-reads both settings files after flushing pending writes. A scope that fails to
    /// reload keeps its previous in-memory values and reports a diagnostic (Pi behaviour);
    /// pending writes are dropped.
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await SaveAsync(cancellationToken);

        var globalLoad = await LoadScopeAsync(_storage, SettingsScope.Global, _paths, cancellationToken);
        if (globalLoad.Error is null)
        {
            _globalRaw = globalLoad.Value;
            _global = DeserializeSettings(_globalRaw);
            _globalLoadError = false;
        }
        else
        {
            _globalLoadError = true;
            _diagnostics.Add(globalLoad.Error);
        }

        ClearModified(_modifiedGlobal, _modifiedGlobalNested);

        if (_projectTrusted)
        {
            var projectLoad = await LoadScopeAsync(_storage, SettingsScope.Project, _paths, cancellationToken);
            if (projectLoad.Error is null)
            {
                _projectRaw = projectLoad.Value;
                _project = DeserializeSettings(_projectRaw);
                _projectLoadError = false;
            }
            else
            {
                _projectLoadError = true;
                _diagnostics.Add(projectLoad.Error!);
            }
        }

        ClearModified(_modifiedProject, _modifiedProjectNested);
        _effective = MergeSettings(_global, _project);
    }

    private static void ClearModified(HashSet<string> fields, Dictionary<string, HashSet<string>> nested)
    {
        fields.Clear();
        foreach (var keys in nested.Values)
        {
            keys.Clear();
        }
    }

    /// <summary>Applies non-persisted overrides on top of the merged settings (CLI flags, tests).</summary>
    public void ApplyOverrides(PiSettings overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        _effective = MergeSettings(_effective, overrides);
    }

    /// <summary>Drains accumulated settings diagnostics in arrival order.</summary>
    public IReadOnlyList<SettingsDiagnostic> DrainDiagnostics()
    {
        var drained = _diagnostics.ToArray();
        _diagnostics.Clear();
        return drained;
    }

    /// <summary>Gets a copy of the resolved global scope settings.</summary>
    public PiSettings GetGlobalSettings() => CloneSettings(_global);

    /// <summary>Gets a copy of the resolved project scope settings.</summary>
    public PiSettings GetProjectSettings() => CloneSettings(_project);

    /// <summary>Gets a copy of the effective merged settings.</summary>
    public PiSettings GetSettings() => CloneSettings(_effective);

    /// <summary>
    /// Persists pending global/project changes using Pi's modified-field merge. Writes are
    /// serialized; failures become diagnostics instead of exceptions (Pi behaviour).
    /// </summary>
    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        lock (_queueLock)
        {
            var previous = _writeQueue;
            var next = previous.ContinueWith(
                _ => PersistChangesCoreAsync(cancellationToken),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _writeQueue = next;
            return next;
        }
    }

    private async Task PersistChangesCoreAsync(CancellationToken cancellationToken)
    {
        // Snapshot both scopes up front: writing one scope must not drop the other's
        // pending fields before it runs.
        var globalFields = SnapshotModified(_modifiedGlobal, _modifiedGlobalNested);
        var projectFields = SnapshotModified(_modifiedProject, _modifiedProjectNested);

        if (globalFields.Fields.Count > 0)
        {
            await PersistScopeAsync(SettingsScope.Global, _globalRaw, globalFields, cancellationToken);
        }

        if (projectFields.Fields.Count > 0)
        {
            if (!_projectTrusted)
            {
                _diagnostics.Add(new SettingsDiagnostic(
                    SettingsScope.Project,
                    _paths.GetValueOrDefault(SettingsScope.Project),
                    "Project is not trusted; refusing to write project settings"));
                ClearModified(_modifiedProject, _modifiedProjectNested);
            }
            else
            {
                await PersistScopeAsync(SettingsScope.Project, _projectRaw, projectFields, cancellationToken);
            }
        }
    }

    private async Task PersistScopeAsync(
        SettingsScope scope,
        JsonObject inMemoryRaw,
        ModifiedSnapshot modified,
        CancellationToken cancellationToken)
    {
        var hadLoadError = scope == SettingsScope.Global ? _globalLoadError : _projectLoadError;
        if (hadLoadError)
        {
            // Never rewrite a file we could not parse; Pi skips the write and keeps the error.
            ClearScopeModified(scope);
            return;
        }

        try
        {
            string? current = await _storage.ReadAsync(scope, cancellationToken);
            var fileSettings = ParseFileSettings(current);
            var merged = new JsonObject();
            if (fileSettings is not null)
            {
                foreach (var (key, value) in fileSettings)
                {
                    merged[key] = value?.DeepClone();
                }
            }

            foreach (var field in modified.Fields)
            {
                var inMemoryValue = inMemoryRaw[field];
                if (modified.Nested.TryGetValue(field, out var nestedKeys) && inMemoryValue is JsonObject inMemoryObject)
                {
                    var baseNested = fileSettings is not null && fileSettings[field] is JsonObject fileObject
                        ? fileObject
                        : new JsonObject();
                    var mergedNested = new JsonObject();
                    foreach (var (key, value) in baseNested)
                    {
                        mergedNested[key] = value?.DeepClone();
                    }
                    foreach (var nestedKey in nestedKeys)
                    {
                        mergedNested[nestedKey] = inMemoryObject[nestedKey]?.DeepClone();
                    }
                    merged[field] = mergedNested;
                }
                else
                {
                    // inMemoryValue may be null (a removed key) or a JSON null (reset value).
                    if (inMemoryValue is null)
                    {
                        merged.Remove(field);
                    }
                    else
                    {
                        merged[field] = inMemoryValue.DeepClone();
                    }
                }
            }

            var json = merged.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            await _storage.WriteAsync(scope, json, cancellationToken);
            ClearScopeModified(scope);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SettingsReadException)
        {
            _diagnostics.Add(new SettingsDiagnostic(scope, _paths.GetValueOrDefault(scope), exception.Message));
        }
    }

    private void ClearScopeModified(SettingsScope scope)
    {
        if (scope == SettingsScope.Global)
        {
            ClearModified(_modifiedGlobal, _modifiedGlobalNested);
        }
        else
        {
            ClearModified(_modifiedProject, _modifiedProjectNested);
        }
    }

    private static ModifiedSnapshot SnapshotModified(
        HashSet<string> fields,
        Dictionary<string, HashSet<string>> nested)
    {
        var snapshot = new ModifiedSnapshot(new HashSet<string>(fields));
        foreach (var (key, value) in nested)
        {
            snapshot.Nested[key] = new HashSet<string>(value);
        }
        return snapshot;
    }

    private sealed class ModifiedSnapshot
    {
        /// <summary>Initializes the snapshot.</summary>
        public ModifiedSnapshot(HashSet<string> fields)
        {
            Fields = fields;
        }

        /// <summary>Gets the modified top-level fields.</summary>
        public HashSet<string> Fields { get; }

        /// <summary>Gets modified nested keys per top-level field.</summary>
        public Dictionary<string, HashSet<string>> Nested { get; } = new();
    }

    private static JsonObject? ParseFileSettings(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var (value, error) = ParseSettingsContent(content, SettingsScope.Global, null);
        return error is null ? value : null;
    }

    private PiSettings CloneSettings(PiSettings settings) =>
        JsonSerializer.Deserialize<PiSettings>(JsonSerializer.SerializeToElement(settings, SerializerOptions), SerializerOptions)!;

    private static PiSettings DeserializeSettings(JsonObject raw) => DeserializeSettingsRaw(raw);

    private static PiSettings DeserializeSettingsRaw(JsonObject raw)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            raw.WriteTo(writer);
        }
        stream.Position = 0;
        return JsonSerializer.Deserialize<PiSettings>(stream, SerializerOptions) ?? new PiSettings();
    }

    /// <summary>
    /// Deep merges two typed settings objects Pi-style: nested objects merge recursively,
    /// every other present value from the override replaces the base value.
    /// </summary>
    internal static PiSettings MergeSettings(PiSettings baseSettings, PiSettings overrides)
    {
        var baseRaw = TypedToRaw(baseSettings);
        var overrideRaw = TypedToRaw(overrides);
        return DeserializeSettingsRaw(DeepMergeRaw(baseRaw, overrideRaw));
    }

    private static JsonObject TypedToRaw(PiSettings settings)
    {
        var element = JsonSerializer.SerializeToElement(settings, SerializerOptions);
        return element.Deserialize<JsonObject>(SerializerOptions) ?? new JsonObject();
    }

    /// <summary>
    /// Deep merges two raw settings objects Pi-style: nested objects merge recursively,
    /// every other value (arrays, scalars, JSON null) from the override replaces the base.
    /// </summary>
    internal static JsonObject DeepMergeRaw(JsonObject baseObject, JsonObject overrides)
    {
        var result = new JsonObject();
        foreach (var (key, value) in baseObject)
        {
            result[key] = value?.DeepClone();
        }

        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                continue;
            }
            if (result[key] is JsonObject baseNested && value is JsonObject overrideNested)
            {
                result[key] = DeepMergeRaw(baseNested, overrideNested);
            }
            else
            {
                result[key] = value.DeepClone();
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Pi settings migrations (settings-manager.migrateSettings)
    // ------------------------------------------------------------------

    /// <summary>Applies Pi's settings format migrations to a parsed settings object.</summary>
    internal static void MigrateSettings(JsonObject settings)
    {
        // queueMode -> steeringMode
        if (settings.ContainsKey("queueMode") && !settings.ContainsKey("steeringMode"))
        {
            settings["steeringMode"] = settings["queueMode"]?.DeepClone();
            settings.Remove("queueMode");
        }

        // legacy websockets boolean -> transport enum
        if (!settings.ContainsKey("transport") &&
            settings["websockets"] is JsonValue websockets &&
            websockets.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            settings["transport"] = websockets.GetValue<bool>() ? "websocket" : "sse";
            settings.Remove("websockets");
        }

        // old skills object format -> new array format
        if (settings["skills"] is JsonObject skills)
        {
            if (skills["enableSkillCommands"] is not null && !settings.ContainsKey("enableSkillCommands"))
            {
                settings["enableSkillCommands"] = skills["enableSkillCommands"]!.DeepClone();
            }

            if (skills["customDirectories"] is JsonArray directories && directories.Count > 0)
            {
                settings["skills"] = directories.DeepClone();
            }
            else
            {
                settings.Remove("skills");
            }
        }

        // retry.maxDelayMs -> retry.provider.maxRetryDelayMs
        if (settings["retry"] is JsonObject retry)
        {
            if (retry["maxDelayMs"] is JsonValue maxDelay &&
                maxDelay.GetValueKind() == JsonValueKind.Number &&
                maxDelay.TryGetValue(out int? delayValue) &&
                delayValue is not null)
            {
                var provider = retry["provider"] is JsonObject existingProvider
                    ? existingProvider
                    : new JsonObject();
                if (provider["maxRetryDelayMs"] is not JsonValue existing ||
                    existing.GetValueKind() is JsonValueKind.Null)
                {
                    provider["maxRetryDelayMs"] = delayValue.Value;
                }
                retry["provider"] = provider;
            }
            retry.Remove("maxDelayMs");
        }
    }

    // ------------------------------------------------------------------
    // Modified-field tracking helpers
    // ------------------------------------------------------------------

    private void MarkGlobalModified(string field, string? nestedKey = null)
    {
        _modifiedGlobal.Add(field);
        if (nestedKey is not null)
        {
            GetOrCreate(_modifiedGlobalNested, field).Add(nestedKey);
        }
    }

    private void MarkProjectModified(string field, string? nestedKey = null)
    {
        _modifiedProject.Add(field);
        if (nestedKey is not null)
        {
            GetOrCreate(_modifiedProjectNested, field).Add(nestedKey);
        }
    }

    private static HashSet<string> GetOrCreate(Dictionary<string, HashSet<string>> nested, string field)
    {
        if (!nested.TryGetValue(field, out var keys))
        {
            keys = new HashSet<string>();
            nested[field] = keys;
        }
        return keys;
    }

    private void SetGlobalRaw(string field, JsonNode? value, string? nestedKey = null)
    {
        ApplyRawUpdate(_globalRaw, field, value, nestedKey);
    }

    private void SetProjectRaw(string field, JsonNode? value, string? nestedKey = null)
    {
        ApplyRawUpdate(_projectRaw, field, value, nestedKey);
    }

    private static void ApplyRawUpdate(JsonObject raw, string field, JsonNode? value, string? nestedKey)
    {
        if (nestedKey is null)
        {
            if (value is null)
            {
                raw.Remove(field);
            }
            else
            {
                raw[field] = value;
            }
            return;
        }

        if (raw[field] is not JsonObject nested)
        {
            nested = new JsonObject();
            raw[field] = nested;
        }

        if (value is null)
        {
            nested.Remove(nestedKey);
        }
        else
        {
            nested[nestedKey] = value;
        }
    }

    /// <summary>Recomputes the effective merged settings after a scope change.</summary>
    private void RecomputeEffective()
    {
        _effective = MergeSettings(_global, _project);
    }

    /// <summary>Serializes a typed value into a raw JSON node for the scope stores.</summary>
    internal static JsonNode JsonFrom(object? value) => value is null
        ? JsonValue.Create<JsonElement?>(null)!
        : JsonSerializer.SerializeToNode(value, SerializerOptions)!;
}
