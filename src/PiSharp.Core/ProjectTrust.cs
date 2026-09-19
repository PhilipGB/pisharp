using System.Text.Json;

namespace PiSharp.Core;

/// <summary>Fallback used when no CLI or saved project trust decision exists.</summary>
public enum DefaultProjectTrust
{
    /// <summary>Ask interactively and deny in non-interactive modes.</summary>
    Ask,
    /// <summary>Trust projects with trust-requiring resources.</summary>
    Always,
    /// <summary>Reject projects with trust-requiring resources.</summary>
    Never,
}

/// <summary>The three states represented by the durable trust store.</summary>
public enum ProjectTrustDecision
{
    /// <summary>No explicit decision exists at this path.</summary>
    NoDecision,
    /// <summary>The path is explicitly trusted.</summary>
    Trusted,
    /// <summary>The path is explicitly denied.</summary>
    NotTrusted,
}

/// <summary>A canonical trust entry found by nearest-ancestor lookup.</summary>
public sealed record ProjectTrustStoreEntry(string Path, bool Trusted);

/// <summary>A trust-store mutation; null removes an explicit decision.</summary>
public sealed record ProjectTrustUpdate(string Path, bool? Decision);

/// <summary>A user-facing trust choice and its durable mutations.</summary>
public sealed record ProjectTrustOption(
    string Label,
    bool Trusted,
    IReadOnlyList<ProjectTrustUpdate> Updates,
    string? SavedPath = null);

/// <summary>Why the current invocation received its effective trust decision.</summary>
public enum ProjectTrustDecisionSource
{
    /// <summary>No trust-requiring project resource was found.</summary>
    NoResources,
    /// <summary>A CLI override selected the decision.</summary>
    CliOverride,
    /// <summary>A nearest saved decision selected the decision.</summary>
    Saved,
    /// <summary>The global default selected the decision.</summary>
    Default,
    /// <summary>An interactive selection selected the decision.</summary>
    Interactive,
    /// <summary>Headless ask mode failed closed.</summary>
    HeadlessAsk,
}

/// <summary>The result of resolving trust for one process invocation.</summary>
public sealed record ProjectTrustResolution(
    bool Trusted,
    bool TrustRequired,
    ProjectTrustDecisionSource Source,
    ProjectTrustStoreEntry? SavedDecision = null);

/// <summary>The app modes relevant to whether a trust prompt may be shown.</summary>
public enum ProjectTrustMode
{
    /// <summary>A line-oriented interactive terminal is available.</summary>
    Interactive,
    /// <summary>Print, JSON, or another non-interactive invocation.</summary>
    NonInteractive,
}

/// <summary>Detected project resources that must be gated by trust.</summary>
public sealed record ProjectTrustResourceDetection(
    bool TrustRequired,
    IReadOnlyList<string> Resources);

/// <summary>
/// Detects project-local Pi and PiSharp resources without loading their contents or assemblies.
/// Ordinary AGENTS.md/CLAUDE.md context files intentionally do not trigger this detector.
/// </summary>
public sealed class ProjectTrustResourceDetector
{
    private static readonly string[] PiResourceNames =
    [
        "settings.json",
        "extensions",
        "skills",
        "prompts",
        "themes",
        "SYSTEM.md",
        "APPEND_SYSTEM.md",
        "packages",
    ];

    /// <summary>Scans the current project and its ancestor .agents/skills directories.</summary>
    public ProjectTrustResourceDetection Detect(string workingDirectory, string? homeDirectory = null)
    {
        var root = ProjectTrustPath.Normalize(workingDirectory);
        var home = ProjectTrustPath.GetHomeDirectory(homeDirectory);
        var resources = new List<string>();
        var projectConfig = Path.Combine(root, ".pi");
        foreach (var resourceName in PiResourceNames)
        {
            var path = Path.Combine(projectConfig, resourceName);
            if (PathExists(path))
            {
                resources.Add(path);
            }
        }

        var globalAgentsSkills = ProjectTrustPath.Normalize(Path.Combine(home, ".agents", "skills"));
        var current = root;
        while (true)
        {
            var agentsSkills = ProjectTrustPath.Normalize(Path.Combine(current, ".agents", "skills"));
            if (!string.Equals(agentsSkills, globalAgentsSkills, ProjectTrustPath.Comparison) && PathExists(agentsSkills))
            {
                resources.Add(agentsSkills);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null)
            {
                break;
            }
            current = ProjectTrustPath.Normalize(parent);
        }

        return new ProjectTrustResourceDetection(resources.Count > 0, resources);
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException($"Unable to inspect project trust resource '{path}'.", exception);
        }
    }
}

/// <summary>
/// Persists PiSharp-owned trust decisions. The file is intentionally separate from Pi's
/// TypeScript trust store because PiSharp executes trusted .NET assemblies.
/// </summary>
public sealed class ProjectTrustStore
{
    private const int LockAttempts = 10;
    private const int LockDelayMilliseconds = 20;
    private readonly string _trustPath;
    private readonly string _lockPath;

    /// <summary>
    /// Serializes this store instance's access within the process: the exclusive lock file
    /// (below) still guards against concurrent processes, and in-process callers never
    /// burn its retry budget fighting each other for the file handle.
    /// </summary>
    private readonly SemaphoreSlim _processLock = new(1, 1);

    /// <summary>Creates a store beneath an application-owned directory.</summary>
    public ProjectTrustStore(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        var directory = ProjectTrustPath.Normalize(applicationDirectory);
        _trustPath = Path.Combine(directory, "trust.json");
        _lockPath = Path.Combine(directory, "trust.json.lock");
    }

    /// <summary>Gets the durable trust file path.</summary>
    public string TrustPath => _trustPath;

    /// <summary>Gets the nearest explicit decision, or no decision.</summary>
    public ProjectTrustDecision GetDecision(string workingDirectory)
    {
        var entry = GetEntry(workingDirectory);
        return entry is null
            ? ProjectTrustDecision.NoDecision
            : entry.Trusted ? ProjectTrustDecision.Trusted : ProjectTrustDecision.NotTrusted;
    }

    /// <summary>Gets the nearest decision as nullable Boolean for Pi-compatible callers.</summary>
    public bool? Get(string workingDirectory) => GetEntry(workingDirectory)?.Trusted;

    /// <summary>Gets the nearest explicit decision, including the path that supplied it.</summary>
    public ProjectTrustStoreEntry? GetEntry(string workingDirectory)
    {
        var target = ProjectTrustPath.Normalize(workingDirectory);
        return WithLock(data => FindNearest(data, target));
    }

    /// <summary>Adds or removes one explicit decision.</summary>
    public void Set(string workingDirectory, bool? decision) => SetMany([new ProjectTrustUpdate(workingDirectory, decision)]);

    /// <summary>Applies mutations atomically without losing unrelated decisions.</summary>
    public void SetMany(IEnumerable<ProjectTrustUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var normalizedUpdates = updates
            .Select(update => new ProjectTrustUpdate(ProjectTrustPath.Normalize(update.Path), update.Decision))
            .ToArray();
        WithLock(data =>
        {
            foreach (var update in normalizedUpdates)
            {
                if (update.Decision is bool decision)
                {
                    data[update.Path] = decision;
                }
                else
                {
                    data.Remove(update.Path);
                }
            }

            WriteTrustFile(data);
            return (object?)null;
        });
    }

    private static ProjectTrustStoreEntry? FindNearest(
        IReadOnlyDictionary<string, bool> data,
        string workingDirectory)
    {
        var current = workingDirectory;
        while (true)
        {
            if (data.TryGetValue(current, out var trusted))
            {
                return new ProjectTrustStoreEntry(current, trusted);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null)
            {
                return null;
            }
            current = ProjectTrustPath.Normalize(parent);
        }
    }

    private T WithLock<T>(Func<Dictionary<string, bool>, T> action)
    {
        var directory = Path.GetDirectoryName(_trustPath)!;
        Directory.CreateDirectory(directory);
        _processLock.Wait();
        try
        {
            using var lockStream = AcquireLock();
            var data = ReadTrustFile();
            return action(data);
        }
        finally
        {
            _processLock.Release();
        }
    }

    private FileStream AcquireLock()
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (attempt < LockAttempts - 1)
            {
                lastException = exception;
                Thread.Sleep(LockDelayMilliseconds);
            }
        }

        throw new IOException($"Unable to acquire project trust lock '{_lockPath}'.", lastException);
    }

    private Dictionary<string, bool> ReadTrustFile()
    {
        try
        {
            _ = File.GetAttributes(_trustPath);
        }
        catch (FileNotFoundException)
        {
            return new Dictionary<string, bool>(ProjectTrustPath.Comparer);
        }
        catch (DirectoryNotFoundException)
        {
            return new Dictionary<string, bool>(ProjectTrustPath.Comparer);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException($"Unable to inspect project trust store '{_trustPath}'.", exception);
        }

        string content;
        try
        {
            content = File.ReadAllText(_trustPath).TrimStart('\uFEFF');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Unable to read project trust store '{_trustPath}'.", exception);
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidTrustFile("expected a JSON object");
            }

            var data = new Dictionary<string, bool>(ProjectTrustPath.Comparer);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw InvalidTrustFile($"value for '{property.Name}' must be boolean");
                }

                var path = ProjectTrustPath.Normalize(property.Name);
                if (data.ContainsKey(path))
                {
                    throw InvalidTrustFile($"duplicate canonical path '{path}'");
                }
                data[path] = property.Value.GetBoolean();
            }
            return data;
        }
        catch (JsonException exception)
        {
            throw InvalidTrustFile(exception.Message, exception);
        }
    }

    private void WriteTrustFile(IReadOnlyDictionary<string, bool> data)
    {
        var sorted = data
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        var temporaryPath = $"{_trustPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _trustPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Unable to write project trust store '{_trustPath}'.", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static InvalidDataException InvalidTrustFile(string reason, Exception? inner = null) =>
        new($"Invalid project trust store: {reason}.", inner);
}

/// <summary>Reads the one global setting required before project trust is resolved.</summary>
public static class GlobalProjectTrustSettings
{
    /// <summary>Reads defaultProjectTrust from a global settings file only.</summary>
    public static (DefaultProjectTrust Value, string? Warning) ReadDefaultProjectTrust(string settingsPath)
    {
        try
        {
            _ = File.GetAttributes(settingsPath);
        }
        catch (FileNotFoundException)
        {
            return (DefaultProjectTrust.Ask, null);
        }
        catch (DirectoryNotFoundException)
        {
            return (DefaultProjectTrust.Ask, null);
        }
        catch (UnauthorizedAccessException exception)
        {
            return (DefaultProjectTrust.Ask, $"Unable to read global project trust setting: {exception.Message}");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath).TrimStart('\uFEFF'));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("defaultProjectTrust", out var value))
            {
                return (DefaultProjectTrust.Ask, null);
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString()?.ToLowerInvariant() switch
                {
                    "always" => (DefaultProjectTrust.Always, null),
                    "never" => (DefaultProjectTrust.Never, null),
                    "ask" => (DefaultProjectTrust.Ask, null),
                    _ => (DefaultProjectTrust.Ask, "Invalid global defaultProjectTrust; using 'ask'."),
                };
            }

            return (DefaultProjectTrust.Ask, "Invalid global defaultProjectTrust; using 'ask'.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return (DefaultProjectTrust.Ask, $"Unable to read global project trust setting: {exception.Message}");
        }
    }
}

/// <summary>Resolves trust before resource discovery or extension assembly loading.</summary>
public sealed class ProjectTrustResolver
{
    /// <summary>Resolves CLI, saved, default, and interactive decisions in that order.</summary>
    public ProjectTrustResolution Resolve(
        string workingDirectory,
        ProjectTrustStore trustStore,
        bool? trustOverride,
        DefaultProjectTrust defaultTrust,
        ProjectTrustMode mode,
        Func<IReadOnlyList<ProjectTrustOption>, ProjectTrustOption?>? selectOption = null,
        string? homeDirectory = null)
    {
        var detection = new ProjectTrustResourceDetector().Detect(workingDirectory, homeDirectory);
        if (!detection.TrustRequired)
        {
            return new ProjectTrustResolution(true, false, ProjectTrustDecisionSource.NoResources);
        }
        if (trustOverride is bool overrideValue)
        {
            return new ProjectTrustResolution(overrideValue, true, ProjectTrustDecisionSource.CliOverride);
        }

        var saved = trustStore.GetEntry(workingDirectory);
        if (saved is not null)
        {
            return new ProjectTrustResolution(saved.Trusted, true, ProjectTrustDecisionSource.Saved, saved);
        }

        if (defaultTrust is DefaultProjectTrust.Always or DefaultProjectTrust.Never)
        {
            return new ProjectTrustResolution(
                defaultTrust == DefaultProjectTrust.Always,
                true,
                ProjectTrustDecisionSource.Default);
        }

        if (mode != ProjectTrustMode.Interactive || selectOption is null)
        {
            return new ProjectTrustResolution(false, true, ProjectTrustDecisionSource.HeadlessAsk);
        }

        var selected = selectOption(GetOptions(workingDirectory, includeSessionOnly: true));
        if (selected is null)
        {
            return new ProjectTrustResolution(false, true, ProjectTrustDecisionSource.Interactive);
        }
        if (selected.Updates.Count > 0)
        {
            trustStore.SetMany(selected.Updates);
        }
        return new ProjectTrustResolution(selected.Trusted, true, ProjectTrustDecisionSource.Interactive);
    }

    /// <summary>Builds Pi-compatible saved and session-only choices for a project.</summary>
    public static IReadOnlyList<ProjectTrustOption> GetOptions(string workingDirectory, bool includeSessionOnly)
    {
        var projectPath = ProjectTrustPath.Normalize(workingDirectory);
        var options = new List<ProjectTrustOption>
        {
            new("Trust", true, [new ProjectTrustUpdate(projectPath, true)], projectPath),
        };
        var parent = Directory.GetParent(projectPath)?.FullName;
        if (parent is not null)
        {
            var parentPath = ProjectTrustPath.Normalize(parent);
            options.Add(new ProjectTrustOption(
                $"Trust parent folder ({parentPath})",
                true,
                [new ProjectTrustUpdate(parentPath, true), new ProjectTrustUpdate(projectPath, null)],
                parentPath));
        }
        if (includeSessionOnly)
        {
            options.Add(new ProjectTrustOption("Trust (this session only)", true, []));
        }
        options.Add(new ProjectTrustOption(
            "Do not trust",
            false,
            [new ProjectTrustUpdate(projectPath, false)],
            projectPath));
        if (includeSessionOnly)
        {
            options.Add(new ProjectTrustOption("Do not trust (this session only)", false, []));
        }
        return options;
    }
}

/// <summary>Path and comparison helpers shared by trust detection and persistence.</summary>
public static class ProjectTrustPath
{
    /// <summary>Uses HOME when available so tests and Unix installations match Pi.</summary>
    public static string GetHomeDirectory(string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetEnvironmentVariable("HOME");
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidOperationException("Unable to determine the user home directory for project trust.");
        }
        return Normalize(home);
    }

    /// <summary>Returns an absolute path with redundant trailing separators removed.</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var canonicalPath = TryResolveLinks(fullPath);
        var root = Path.GetPathRoot(canonicalPath);
        return root is not null && string.Equals(canonicalPath, root, Comparison)
            ? root
            : canonicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string TryResolveLinks(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        var segments = fullPath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            current = ResolveLink(candidate) ?? candidate;
        }
        return current;
    }

    private static string? ResolveLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Returns the application-owned PiSharp trust directory.</summary>
    public static string GetDefaultTrustDirectory(string? homeDirectory = null) =>
        Path.Combine(GetHomeDirectory(homeDirectory), ".pisharp");

    /// <summary>Returns Pi's global settings location without reading project settings.</summary>
    public static string GetGlobalPiSettingsPath(string? homeDirectory = null) =>
        Path.Combine(GetHomeDirectory(homeDirectory), ".pi", "agent", "settings.json");

    // Linux is the only supported platform: case-sensitive path semantics.
    internal static readonly StringComparer Comparer = StringComparer.Ordinal;

    internal static readonly StringComparison Comparison = StringComparison.Ordinal;
}
