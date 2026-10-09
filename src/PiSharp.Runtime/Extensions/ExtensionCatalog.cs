using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Tools;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Runtime.Extensions;

/// <summary>Native plugin entry point. Plugin code has the full OS permissions of PiSharp.</summary>
public interface IPiSharpExtension
{
    void Configure(ExtensionRegistration registration);
}

/// <summary>Direct RPC Bash request offered to loaded native extensions.</summary>
public sealed record UserBashContext(string Command, bool ExcludeFromContext, string WorkingDirectory,
    Func<string, Task> EmitUpdateAsync);

public delegate Task<BashExecutionResult?> UserBashHandler(UserBashContext context, CancellationToken cancellationToken);

public sealed record ExtensionCommandInfo(string? Description, ResourceSourceInfo SourceInfo);

/// <summary>An MCP server declared by a loaded extension. It is scoped to this runtime session.</summary>
public sealed record ExtensionMcpServerRegistration(McpServerConfiguration Configuration, string ExtensionPath);

/// <summary>A capability registered through the same extension surface as loaded assemblies.</summary>
public sealed record BuiltinExtensionDefinition(string Name, Action<ExtensionRegistration> Configure,
    bool AutoEnable = true, Func<ExtensionRegistration, bool>? ShouldAutoEnable = null);

public sealed class ExtensionRegistration
{
    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal)
    {
        "tree", "branch", "fork", "clone", "new", "sessions", "resume", "name", "model", "models",
        "compact", "export", "export-jsonl", "import", "session", "trust", "reload", "quit", "exit"
    };
    private readonly Dictionary<string, AIFunction> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PiSharpToolRegistration> _toolDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResourceSourceInfo> _toolSourceInfo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PiSharpToolRenderer> _toolRenderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _ownedToolNames = new(StringComparer.Ordinal);
    private readonly object _toolGate = new();
    private readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionCommandInfo> _commandInfo = new(StringComparer.Ordinal);
    private readonly List<UserBashHandler> _userBashHandlers = [];
    private readonly List<PiSharpToolCallHook> _toolCallHooks = [];
    private readonly List<PiSharpToolResultHook> _toolResultHooks = [];
    private readonly List<PiSharpContextTransform> _contextTransforms = [];
    private readonly List<RegisteredResourceDiscoveryHandler> _resourceDiscoveryHandlers = [];
    private readonly object _mcpGate = new();
    private readonly Dictionary<string, ExtensionMcpServerRegistration> _mcpServers = new(StringComparer.Ordinal);
    private readonly VirtualModelRegistry _virtualModels = new();
    private ResourceSourceInfo? _currentSourceInfo;
    public IReadOnlyCollection<AIFunction> Tools { get { lock (_toolGate) return _tools.Values.ToArray(); } }
    public IReadOnlyCollection<PiSharpToolRegistration> ToolDefinitions
    {
        get { lock (_toolGate) return _toolDefinitions.Values.ToArray(); }
    }
    public IReadOnlyDictionary<string, ResourceSourceInfo> ToolSourceInfo
    {
        get { lock (_toolGate) return new Dictionary<string, ResourceSourceInfo>(_toolSourceInfo, StringComparer.Ordinal); }
    }
    public IReadOnlyDictionary<string, PiSharpToolRenderer> ToolRenderers
    {
        get { lock (_toolGate) return new Dictionary<string, PiSharpToolRenderer>(_toolRenderers, StringComparer.Ordinal); }
    }
    public IReadOnlyDictionary<string, Func<string, CancellationToken, Task<string>>> Commands => _commands;
    public IReadOnlyDictionary<string, ExtensionCommandInfo> CommandInfo => _commandInfo;
    public IReadOnlyList<UserBashHandler> UserBashHandlers => _userBashHandlers;
    public IReadOnlyList<PiSharpToolCallHook> ToolCallHooks => _toolCallHooks;
    public IReadOnlyList<PiSharpToolResultHook> ToolResultHooks => _toolResultHooks;
    public IReadOnlyList<PiSharpContextTransform> ContextTransforms => _contextTransforms.ToArray();
    internal IReadOnlyList<RegisteredResourceDiscoveryHandler> ResourceDiscoveryHandlers =>
        _resourceDiscoveryHandlers.ToArray();
    public IReadOnlyCollection<ExtensionMcpServerRegistration> McpServers
    {
        get { lock (_mcpGate) return _mcpServers.Values.ToArray(); }
    }
    public VirtualModelRegistry VirtualModels => _virtualModels;
    internal event Action<IReadOnlyCollection<PiSharpToolRegistration>>? ToolDefinitionsChanged;

    /// <summary>Registers an MCP server for this session using the same shape as an mcp.json entry.</summary>
    public void RegisterMcpServer(string name, JsonElement configuration)
    {
        var source = _currentSourceInfo ?? throw new InvalidOperationException(
            "MCP servers can only be registered while an extension is being configured.");
        var owner = source.Path;
        var parsed = McpConfiguration.Parse(name, configuration, owner, "extension");
        lock (_mcpGate)
        {
            var namespaceConflict = _mcpServers.Values.FirstOrDefault(existing =>
                existing.Configuration.Name != name &&
                McpToolIdentifiers.Namespace(existing.Configuration.Name) == McpToolIdentifiers.Namespace(name));
            if (namespaceConflict is not null)
                throw new InvalidOperationException($"MCP server {name} conflicts with registered server " +
                    $"{namespaceConflict.Configuration.Name} because both names use the same normalized namespace.");
            if (_mcpServers.TryGetValue(name, out var existing) && existing.ExtensionPath != owner)
                throw new InvalidOperationException($"MCP server {name} is already registered by extension {existing.ExtensionPath}.");
            _mcpServers[name] = new(parsed, owner);
        }
    }

    /// <summary>Removes an MCP server registered by the extension currently being configured.</summary>
    public void UnregisterMcpServer(string name)
    {
        var source = _currentSourceInfo ?? throw new InvalidOperationException(
            "MCP servers can only be unregistered while an extension is being configured.");
        lock (_mcpGate)
            if (_mcpServers.TryGetValue(name, out var existing) && existing.ExtensionPath == source.Path)
                _mcpServers.Remove(name);
    }

    /// <summary>Registers a logical model whose router selects a credentialed physical model per request.</summary>
    public void RegisterVirtualModel(VirtualModelDefinition definition)
    {
        var source = _currentSourceInfo ?? throw new InvalidOperationException(
            "Virtual models can only be registered while an extension is being configured.");
        _virtualModels.Register(definition, source.Path);
    }

    /// <summary>Removes a virtual model registered by the extension currently being configured.</summary>
    public void UnregisterVirtualModel(string provider, string id)
    {
        var source = _currentSourceInfo ?? throw new InvalidOperationException(
            "Virtual models can only be unregistered while an extension is being configured.");
        _virtualModels.Unregister(provider, id, source.Path);
    }

    public void AddTool(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        AddTool(new PiSharpToolRegistration(tool));
    }

    /// <summary>Registers a tool with exposure, namespace, and loadout behavior.</summary>
    public void AddTool(PiSharpToolRegistration definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Function);
        IReadOnlyCollection<PiSharpToolRegistration> changed;
        lock (_toolGate)
        {
            AddToolCore(definition);
            changed = _toolDefinitions.Values.ToArray();
        }
        ToolDefinitionsChanged?.Invoke(changed);
    }

    /// <summary>Registers an extension tool with optional safe terminal call/result renderers.</summary>
    public void AddTool(AIFunction tool, PiSharpToolRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(renderer);
        IReadOnlyCollection<PiSharpToolRegistration> changed;
        lock (_toolGate)
        {
            var definition = new PiSharpToolRegistration(tool);
            AddToolCore(definition);
            _toolRenderers.Add(tool.Name, renderer);
            changed = _toolDefinitions.Values.ToArray();
        }
        ToolDefinitionsChanged?.Invoke(changed);
    }

    /// <summary>Registers a configured extension tool with optional safe terminal renderers.</summary>
    public void AddTool(PiSharpToolRegistration definition, PiSharpToolRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(renderer);
        IReadOnlyCollection<PiSharpToolRegistration> changed;
        lock (_toolGate)
        {
            AddToolCore(definition);
            _toolRenderers.Add(definition.Function.Name, renderer);
            changed = _toolDefinitions.Values.ToArray();
        }
        ToolDefinitionsChanged?.Invoke(changed);
    }

    public PiSharpToolRenderer? GetToolRenderer(string name)
    {
        lock (_toolGate) return _toolRenderers.TryGetValue(name, out var renderer) ? renderer : null;
    }

    internal IReadOnlyCollection<string> GetOwnedToolNames(string owner)
    {
        lock (_toolGate) return _ownedToolNames.TryGetValue(owner, out var names) ? names.ToArray() : [];
    }

    internal void ReplaceOwnedTools(string owner, IEnumerable<PiSharpToolRegistration> definitions,
        ResourceSourceInfo sourceInfo, IReadOnlyDictionary<string, PiSharpToolRenderer>? renderers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(sourceInfo);
        var replacements = definitions.ToArray();
        var byName = new Dictionary<string, PiSharpToolRegistration>(StringComparer.Ordinal);
        foreach (var definition in replacements)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(definition.Function);
            if (!byName.TryAdd(definition.Function.Name, definition))
                throw new ArgumentException("Duplicate owned extension tool: " + definition.Function.Name, nameof(definitions));
        }
        if (renderers is not null && renderers.Keys.Any(name => !byName.ContainsKey(name)))
            throw new ArgumentException("A renderer was supplied for an unregistered tool.", nameof(renderers));

        IReadOnlyCollection<PiSharpToolRegistration> changed;
        lock (_toolGate)
        {
            var previous = _ownedToolNames.TryGetValue(owner, out var names)
                ? new HashSet<string>(names, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in byName.Keys)
                if (_toolDefinitions.ContainsKey(name) && !previous.Contains(name))
                    throw new ArgumentException($"Duplicate extension tool: {name}", nameof(definitions));

            foreach (var name in previous.Except(byName.Keys, StringComparer.Ordinal))
            {
                var existing = _toolDefinitions[name];
                _toolDefinitions[name] = existing with
                {
                    Exposure = ToolExposure.Hidden,
                    DefaultActive = false,
                    AllowNestedInvocation = false
                };
            }

            foreach (var (name, definition) in byName)
            {
                _tools[name] = definition.Function;
                _toolDefinitions[name] = definition;
                _toolSourceInfo[name] = sourceInfo;
                if (renderers?.TryGetValue(name, out var renderer) == true)
                    _toolRenderers[name] = renderer;
                else _toolRenderers.Remove(name);
            }

            previous.UnionWith(byName.Keys);
            _ownedToolNames[owner] = previous;
            changed = _toolDefinitions.Values.ToArray();
        }
        ToolDefinitionsChanged?.Invoke(changed);
    }

    /// <summary>Registers an asynchronous policy hook for model-issued and nested tool calls.</summary>
    public void AddToolCallHook(PiSharpToolCallHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _toolCallHooks.Add(hook);
    }

    /// <summary>Registers an asynchronous result hook for model-issued and nested tool calls.</summary>
    public void AddToolResultHook(PiSharpToolResultHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _toolResultHooks.Add(hook);
    }

    /// <summary>Registers an asynchronous transform applied to provider-bound conversation context.</summary>
    public void AddContextTransform(PiSharpContextTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _contextTransforms.Add(transform);
    }

    /// <summary>Registers resources that become available after extension startup and each reload.</summary>
    public void AddResourceDiscoveryHandler(ExtensionResourceDiscoveryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var source = _currentSourceInfo ?? throw new InvalidOperationException(
            "Resource discovery handlers can only be registered while an extension is being configured.");
        _resourceDiscoveryHandlers.Add(new(handler, source));
    }

    public void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler) =>
        AddCommand(name, handler, null);

    public void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler, string? description)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Extension command names must be alphanumeric, '_' or '-'.", nameof(name));
        if (s_reserved.Contains(name) || !_commands.TryAdd(name, handler))
            throw new ArgumentException($"Reserved or duplicate extension command: {name}");
        var sourceInfo = _currentSourceInfo ?? SourceInfoFromHandler(handler);
        _commandInfo.Add(name, new(description, sourceInfo));
    }

    internal void SetCurrentSourceInfo(ResourceSourceInfo? sourceInfo) => _currentSourceInfo = sourceInfo;

    public void SetToolDefaultActive(string name, bool active)
    {
        IReadOnlyCollection<PiSharpToolRegistration> changed;
        lock (_toolGate)
        {
            if (!_toolDefinitions.TryGetValue(name, out var definition))
                throw new ArgumentException("Unknown tool: " + name, nameof(name));
            _toolDefinitions[name] = definition with { DefaultActive = active };
            changed = _toolDefinitions.Values.ToArray();
        }
        ToolDefinitionsChanged?.Invoke(changed);
    }

    private void AddToolCore(PiSharpToolRegistration definition)
    {
        var name = definition.Function.Name;
        if (!_tools.TryAdd(name, definition.Function))
            throw new ArgumentException($"Duplicate extension tool: {name}");
        _toolDefinitions.Add(name, definition);
        var assemblyPath = definition.Function.GetType().Assembly.Location;
        _toolSourceInfo.Add(name, _currentSourceInfo ??
            (string.IsNullOrWhiteSpace(assemblyPath)
                ? new("<unknown>", "local", "temporary", "top-level", null)
                : new(Path.GetFullPath(assemblyPath), "local", "temporary", "top-level",
                    Path.GetDirectoryName(assemblyPath))));
    }

    private static ResourceSourceInfo SourceInfoFromHandler(Delegate handler)
    {
        var path = handler.Method.DeclaringType?.Assembly.Location;
        return string.IsNullOrWhiteSpace(path)
            ? new("<unknown>", "local", "temporary", "top-level", null)
            : new(Path.GetFullPath(path), "local", "temporary", "top-level", Path.GetDirectoryName(path));
    }

    /// <summary>Registers a handler for direct RPC Bash. Return null to let the next handler or shell run.</summary>
    public void AddUserBashHandler(UserBashHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _userBashHandlers.Add(handler);
    }
}

/// <summary>Owns the active plugin set and disposes prior contexts on a successful reload.</summary>
public sealed class ExtensionLease(ExtensionCatalog initial) : IDisposable
{
    public ExtensionCatalog Current { get; private set; } = initial;
    public void Replace(ExtensionCatalog next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var previous = Current;
        Current = next;
        previous.Dispose();
    }
    public void Dispose() => Current.Dispose();
}

/// <summary>Loads personal and trust-gated project assemblies, plus caller-selected paths, in isolated contexts for reload.</summary>
public sealed class ExtensionCatalog : IDisposable
{
    private readonly List<AssemblyLoadContext> _contexts = [];
    private readonly List<IAsyncDisposable> _ownedConnections = [];
    private readonly List<ResourceSourceInfo> _loadedExtensions = [];
    private readonly HashSet<string> _loadedBuiltins = new(StringComparer.Ordinal);
    public ExtensionRegistration Registration { get; } = new();
    public IReadOnlySet<string> LoadedBuiltins => _loadedBuiltins;
    public IReadOnlyList<ResourceSourceInfo> LoadedExtensions => _loadedExtensions.ToArray();

    public async Task<ExtensionResourceDiscovery> DiscoverResourcesAsync(string workingDirectory,
        ExtensionResourceDiscoveryReason reason, CancellationToken cancellationToken = default)
    {
        var cwd = Path.GetFullPath(workingDirectory);
        var skills = new List<ExtensionDiscoveredResourcePath>();
        var prompts = new List<ExtensionDiscoveredResourcePath>();
        var themes = new List<ExtensionDiscoveredResourcePath>();
        var errors = new List<ExtensionResourceDiscoveryError>();
        var context = new ExtensionResourceDiscoveryContext(cwd, reason);

        foreach (var registration in Registration.ResourceDiscoveryHandlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await registration.Handler(context, cancellationToken);
                if (result is null) continue;
                AddPaths(result.SkillPaths, skills, registration.ExtensionSource, cwd);
                AddPaths(result.PromptPaths, prompts, registration.ExtensionSource, cwd);
                AddPaths(result.ThemePaths, themes, registration.ExtensionSource, cwd);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                errors.Add(new(registration.ExtensionSource.Path, error.Message));
            }
        }

        return new(skills, prompts, themes, errors);
    }

    private static void AddPaths(IReadOnlyList<string>? paths, List<ExtensionDiscoveredResourcePath> target,
        ResourceSourceInfo extensionSource, string workingDirectory)
    {
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var normalized = Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile
                ? Path.GetFullPath(uri.LocalPath)
                : Path.GetFullPath(path, workingDirectory);
            var synthetic = extensionSource.Path.StartsWith("builtin:", StringComparison.Ordinal) ||
                extensionSource.Path.StartsWith('<');
            var label = synthetic
                ? extensionSource.Path.Trim('<', '>')
                : Path.GetFileNameWithoutExtension(extensionSource.Path);
            var source = new ResourceSourceInfo(normalized, "extension:" + label, "temporary", "top-level",
                synthetic ? null : Path.GetDirectoryName(Path.GetFullPath(extensionSource.Path)));
            target.Add(new(normalized, source));
        }
    }

    private ExtensionCatalog() { }

    public void OwnConnection(IAsyncDisposable connection) => _ownedConnections.Add(connection);

    public void EnableBuiltinIfAllowed(BuiltinExtensionDefinition builtin,
        IReadOnlyList<string>? userPaths, IReadOnlyList<string>? projectPaths)
    {
        if (_loadedBuiltins.Contains(builtin.Name) || !IsBuiltinEnabled(builtin.Name, userPaths, projectPaths) ||
            Registration.ToolDefinitions.Any(tool => tool.Function.Name ==
                (builtin.Name == "tool-search" ? "tool_search" : builtin.Name))) return;
        Registration.SetCurrentSourceInfo(new("builtin:" + builtin.Name, "builtin", "builtin", "top-level", null));
        try
        {
            builtin.Configure(Registration);
            _loadedBuiltins.Add(builtin.Name);
        }
        finally { Registration.SetCurrentSourceInfo(null); }
    }

    public static ExtensionCatalog Load(string agentDirectory, string cwd, bool projectTrusted, bool discover = true,
        IReadOnlyList<string>? additionalPaths = null, IReadOnlyList<string>? userPaths = null,
        IReadOnlyList<string>? projectPaths = null, IReadOnlyList<BuiltinExtensionDefinition>? builtins = null)
    {
        var catalog = new ExtensionCatalog();
        try
        {
            var selectedPaths = new List<(string Path, ResourceSourceInfo SourceInfo)>();
            var selectedBuiltins = new List<BuiltinExtensionDefinition>();
            var builtinByName = (builtins ?? []).ToDictionary(builtin => builtin.Name, StringComparer.Ordinal);
            var seenBuiltins = new HashSet<string>(StringComparer.Ordinal);
            var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var entry in additionalPaths ?? [])
            {
                if (entry.StartsWith("builtin:", StringComparison.Ordinal))
                {
                    AddBuiltin(entry);
                    continue;
                }
                var path = Path.GetFullPath(entry, cwd);
                IEnumerable<string> paths;
                if (Directory.Exists(path)) paths = FindAssemblies(path);
                else if (File.Exists(path) && Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    paths = [path];
                else throw new FileNotFoundException("Explicit extension must be a .dll file or an existing directory.", path);
                foreach (var assemblyPath in paths)
                    AddPath(assemblyPath, new(Path.GetFullPath(assemblyPath), "cli", "temporary", "top-level",
                        null));
            }
            if (projectTrusted)
                AddConfiguredPaths(projectPaths, Path.Combine(cwd, ".pi"), "project");
            AddConfiguredPaths(userPaths, agentDirectory, "user");
            if (discover)
            {
                if (projectTrusted)
                {
                    var projectRoot = Path.Combine(cwd, ".pi", "extensions");
                    if (Directory.Exists(projectRoot))
                        foreach (var path in LocalResourcePathRules.ApplyOverrides(FindAssemblies(projectRoot), projectPaths,
                            Path.GetFullPath(Path.Combine(cwd, ".pi"))))
                            AddPath(path, new(Path.GetFullPath(path), "auto", "project", "top-level",
                                Path.GetFullPath(Path.Combine(cwd, ".pi"))));
                }
                var userRoot = Path.Combine(agentDirectory, "extensions");
                if (Directory.Exists(userRoot))
                    foreach (var path in LocalResourcePathRules.ApplyOverrides(FindAssemblies(userRoot), userPaths,
                        Path.GetFullPath(agentDirectory)))
                        AddPath(path, new(Path.GetFullPath(path), "auto", "user", "top-level", Path.GetFullPath(agentDirectory)));
            }
            foreach (var (path, sourceInfo) in selectedPaths)
            {
                catalog._loadedExtensions.Add(sourceInfo with { Path = path });
                var context = new PluginLoadContext(path);
                catalog._contexts.Add(context);
                var assembly = context.LoadFromAssemblyPath(path);
                foreach (var type in assembly.GetExportedTypes().Where(type =>
                    typeof(IPiSharpExtension).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface))
                {
                    if (Activator.CreateInstance(type) is not IPiSharpExtension extension)
                        throw new InvalidDataException($"Extension {type.FullName} needs a public parameterless constructor.");
                    catalog.Registration.SetCurrentSourceInfo(sourceInfo);
                    try { extension.Configure(catalog.Registration); }
                    finally { catalog.Registration.SetCurrentSourceInfo(null); }
                }
            }
            if (discover)
                foreach (var builtin in builtins ?? [])
                    if (builtin.AutoEnable && (builtin.ShouldAutoEnable?.Invoke(catalog.Registration) ?? true) &&
                        IsBuiltinEnabled(builtin.Name, userPaths, projectTrusted ? projectPaths : null))
                        AddBuiltin("builtin:" + builtin.Name);
            foreach (var builtin in selectedBuiltins)
            {
                catalog.Registration.SetCurrentSourceInfo(new("builtin:" + builtin.Name, "builtin", "builtin",
                    "top-level", null));
                try
                {
                    builtin.Configure(catalog.Registration);
                    catalog._loadedBuiltins.Add(builtin.Name);
                }
                finally { catalog.Registration.SetCurrentSourceInfo(null); }
            }
            return catalog;

            void AddBuiltin(string identifier)
            {
                var name = identifier["builtin:".Length..];
                if (!builtinByName.TryGetValue(name, out var builtin))
                    throw new FileNotFoundException($"Unknown built-in extension: {identifier}", identifier);
                if (seenBuiltins.Add(name)) selectedBuiltins.Add(builtin);
            }

            void AddPath(string path, ResourceSourceInfo sourceInfo)
            {
                var fullPath = Path.GetFullPath(path);
                if (seenPaths.Add(fullPath)) selectedPaths.Add((fullPath, sourceInfo with { Path = fullPath }));
            }

            void AddConfiguredPaths(IReadOnlyList<string>? entries, string baseDirectory, string scope)
            {
                var fullBase = Path.GetFullPath(baseDirectory);
                var candidates = LocalResourcePathRules.GetPaths(entries)
                    .Where(entry => !entry.StartsWith("builtin:", StringComparison.Ordinal))
                    .SelectMany(entry => EnumerateConfiguredAssemblies(LocalResourcePathRules.ResolvePath(entry, fullBase)))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
                foreach (var path in LocalResourcePathRules.ApplyOverrides(candidates, entries, fullBase))
                    AddPath(path, new(Path.GetFullPath(path), "local", scope, "top-level", fullBase));
                foreach (var entry in LocalResourcePathRules.GetPaths(entries).Where(entry =>
                    entry.StartsWith("builtin:", StringComparison.Ordinal)))
                    if (IsBuiltinEnabled(entry["builtin:".Length..], userPaths, projectTrusted ? projectPaths : null))
                        AddBuiltin(entry);
            }
        }
        catch { catalog.Dispose(); throw; }
    }

    private static bool IsBuiltinEnabled(string name, IReadOnlyList<string>? userPaths,
        IReadOnlyList<string>? projectPaths)
    {
        var identity = "builtin:" + name;
        var overrides = LocalResourcePathRules.GetOverrides(userPaths)
            .Concat(LocalResourcePathRules.GetOverrides(projectPaths)).ToArray();
        if (overrides.Any(entry => entry[0] == '-' && entry[1..] == identity)) return false;
        if (overrides.Any(entry => entry[0] == '+' && entry[1..] == identity)) return true;
        return !overrides.Any(entry => entry[0] == '!' &&
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(entry.AsSpan(1), identity,
                ignoreCase: false));
    }

    private static IEnumerable<string> FindAssemblies(string root) =>
        Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(root).Select(path => Path.Combine(path, "index.dll"))
                .Where(File.Exists)).Order(StringComparer.Ordinal).Take(100).Select(Path.GetFullPath).ToArray();

    private static IEnumerable<string> EnumerateConfiguredAssemblies(string root)
    {
        if (File.Exists(root))
            return Path.GetExtension(root).Equals(".dll", StringComparison.OrdinalIgnoreCase) ? [Path.GetFullPath(root)] : [];
        if (!Directory.Exists(root)) return [];
        var rootIndex = Path.Combine(root, "index.dll");
        if (File.Exists(rootIndex)) return [Path.GetFullPath(rootIndex)];
        return FindAssemblies(root);
    }

    public void Dispose()
    {
        foreach (var connection in _ownedConnections)
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _ownedConnections.Clear();
        foreach (var context in _contexts) context.Unload();
        _contexts.Clear();
    }

    private sealed class PluginLoadContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name)))
                return null; // Share PiSharp and MEAI contracts with the host.
            var resolved = _resolver.ResolveAssemblyToPath(name);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }
    }
}
