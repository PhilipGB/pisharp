using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Mcp;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
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

public sealed record ExtensionConflictDiagnostic(string Path, string Error);

public sealed record ExtensionLoadWarning(string Path, string Warning);

internal sealed record RegisteredExtensionCommand(string Name,
    Func<string, CancellationToken, Task<string>>? Handler,
    PiSharp.Extensions.Abstractions.PiSharpExtensionCommandHandler? ContextHandler,
    ExtensionCommandInfo Info);

internal sealed record RegisteredExtensionTool(PiSharpToolRegistration Definition,
    ResourceSourceInfo SourceInfo, PiSharpToolRenderer? Renderer);

/// <summary>An MCP server declared by a loaded extension. It is scoped to this runtime session.</summary>
public sealed record ExtensionMcpServerRegistration(McpServerConfiguration Configuration, string ExtensionPath);

/// <summary>A capability registered through the same extension surface as loaded assemblies.</summary>
public sealed record BuiltinExtensionDefinition(string Name, Action<ExtensionRegistration> Configure,
    bool AutoEnable = true, Func<ExtensionRegistration, bool>? ShouldAutoEnable = null, bool Replaceable = false);

public sealed partial class ExtensionRegistration
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
    private readonly List<RegisteredExtensionCommand> _commands = [];
    private readonly Dictionary<string, List<RegisteredExtensionTool>> _toolCandidates = new(StringComparer.Ordinal);
    private readonly List<ExtensionHandlerRegistration<UserBashHandler>> _userBashHandlers = [];
    private readonly List<ExtensionHandlerRegistration<PiSharpToolCallHook>> _toolCallHooks = [];
    private readonly List<ExtensionHandlerRegistration<PiSharpToolResultHook>> _toolResultHooks = [];
    private readonly List<ExtensionHandlerRegistration<PiSharpContextTransform>> _contextTransforms = [];
    private readonly List<ExtensionLifecycleHandlerRegistration> _lifecycleHandlers = [];
    private readonly List<PiSharpBeforeAgentStartHandlerRegistration> _beforeAgentStartHandlers = [];
    private readonly List<RegisteredResourceDiscoveryHandler> _resourceDiscoveryHandlers = [];
    private readonly List<RegisteredProjectTrustHandler> _projectTrustHandlers = [];
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
    public IReadOnlyDictionary<string, Func<string, CancellationToken, Task<string>>> Commands => ResolveCommands()
        .Where(entry => entry.Command.Handler is not null)
        .ToDictionary(entry => entry.Name, entry => entry.Command.Handler!, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, PiSharp.Extensions.Abstractions.PiSharpExtensionCommandHandler> ContextCommands =>
        ResolveCommands().Where(entry => entry.Command.ContextHandler is not null)
            .ToDictionary(entry => entry.Name, entry => entry.Command.ContextHandler!, StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ExtensionCommandInfo> CommandInfo => ResolveCommands()
        .ToDictionary(entry => entry.Name, entry => entry.Command.Info, StringComparer.Ordinal);
    public IReadOnlyList<UserBashHandler> UserBashHandlers => _userBashHandlers.Select(entry => entry.Handler).ToArray();
    public IReadOnlyList<PiSharpToolCallHook> ToolCallHooks => _toolCallHooks.Select(entry => entry.Handler).ToArray();
    public IReadOnlyList<PiSharpToolResultHook> ToolResultHooks => _toolResultHooks.Select(entry => entry.Handler).ToArray();
    public IReadOnlyList<PiSharpContextTransform> ContextTransforms => _contextTransforms.Select(entry => entry.Handler).ToArray();
    public IReadOnlyList<PiSharpExtensionLifecycleHandler> LifecycleHandlers =>
        _lifecycleHandlers.Select(entry => entry.Handler).ToArray();
    public IReadOnlyList<PiSharpBeforeAgentStartHandlerRegistration> BeforeAgentStartHandlers =>
        _beforeAgentStartHandlers.ToArray();
    internal IReadOnlyList<ExtensionLifecycleHandlerRegistration> RegisteredLifecycleHandlers =>
        _lifecycleHandlers.ToArray();
    internal IReadOnlyList<RegisteredResourceDiscoveryHandler> ResourceDiscoveryHandlers =>
        _resourceDiscoveryHandlers.ToArray();
    private IReadOnlyList<RegisteredProjectTrustHandler> ProjectTrustHandlers => _projectTrustHandlers.ToArray();
    public IReadOnlyCollection<ExtensionMcpServerRegistration> McpServers
    {
        get { lock (_mcpGate) return _mcpServers.Values.ToArray(); }
    }
    public VirtualModelRegistry VirtualModels => _virtualModels;
    internal event Action<IReadOnlyCollection<PiSharpToolRegistration>>? ToolDefinitionsChanged;

    internal void ReleaseExtensionReferences()
    {
        lock (_toolGate)
        {
            _tools.Clear();
            _toolDefinitions.Clear();
            _toolSourceInfo.Clear();
            _toolRenderers.Clear();
            _ownedToolNames.Clear();
            _toolCandidates.Clear();
            ToolDefinitionsChanged = null;
        }
        _commands.Clear();
        _extensionFlags.Clear();
        _shortcuts.Clear();
        _userBashHandlers.Clear();
        _toolCallHooks.Clear();
        _toolResultHooks.Clear();
        _contextTransforms.Clear();
        _lifecycleHandlers.Clear();
        _beforeAgentStartHandlers.Clear();
        _sessionStartHandlers.Clear();
        _sessionShutdownHandlers.Clear();
        _sessionInfoChangedHandlers.Clear();
        _sessionCompactHandlers.Clear();
        _sessionCompactFailedHandlers.Clear();
        _agentStartHandlers.Clear();
        _agentEndHandlers.Clear();
        _turnStartHandlers.Clear();
        _turnEndHandlers.Clear();
        _toolExecutionStartHandlers.Clear();
        _toolExecutionUpdateHandlers.Clear();
        _toolExecutionEndHandlers.Clear();
        _modelSelectHandlers.Clear();
        _thinkingLevelSelectHandlers.Clear();
        _resourceDiscoveryHandlers.Clear();
        _projectTrustHandlers.Clear();
        lock (_mcpGate) _mcpServers.Clear();
        ClearExtensionProviders();
        _virtualModels.Clear();
        _currentSourceInfo = null;
    }

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
                    $"{namespaceConflict.Configuration.Name} because both names use the same normalized namespace " +
                    $"(owned by {namespaceConflict.ExtensionPath}; conflicting extension {owner}).");
            if (_mcpServers.TryGetValue(name, out var existing) && existing.ExtensionPath != owner)
                throw new InvalidOperationException($"MCP server {name} is already registered by extension " +
                    $"{existing.ExtensionPath}; conflicting extension {owner}.");
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
            AddToolCore(definition, null);
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
            AddToolCore(definition, renderer);
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
            AddToolCore(definition, renderer);
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
        _toolCallHooks.Add(new(hook, _currentSourceInfo ?? SourceInfoFromHandler(hook)));
    }

    /// <summary>Registers an asynchronous result hook for model-issued and nested tool calls.</summary>
    public void AddToolResultHook(PiSharpToolResultHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _toolResultHooks.Add(new(hook, _currentSourceInfo ?? SourceInfoFromHandler(hook)));
    }

    /// <summary>Registers an asynchronous transform applied to provider-bound conversation context.</summary>
    public void AddContextTransform(PiSharpContextTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _contextTransforms.Add(new(transform, _currentSourceInfo ?? SourceInfoFromHandler(transform)));
    }

    /// <summary>Registers a synchronous callback for agent lifecycle events emitted during a run.</summary>
    public void AddLifecycleHandler(PiSharpExtensionLifecycleHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _lifecycleHandlers.Add(new(handler, _currentSourceInfo ?? SourceInfoFromHandler(handler)));
    }

    /// <summary>Registers an event-only lifecycle callback.</summary>
    public void AddLifecycleHandler(Action<AgentLifecycleEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var sourceInfo = _currentSourceInfo ?? SourceInfoFromHandler(handler);
        _lifecycleHandlers.Add(new((eventData, _) => handler(eventData), sourceInfo));
    }

    /// <summary>Registers an asynchronous hook that may replace the system prompt for each user turn.</summary>
    public void AddBeforeAgentStartHandler(PiSharpBeforeAgentStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _beforeAgentStartHandlers.Add(new(handler, _currentSourceInfo ?? SourceInfoFromHandler(handler)));
    }

    /// <summary>Registers resources that become available after extension startup and each reload.</summary>
    public void AddResourceDiscoveryHandler(ExtensionResourceDiscoveryHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var source = _currentSourceInfo ?? throw new InvalidOperationException(
            "Resource discovery handlers can only be registered while an extension is being configured.");
        _resourceDiscoveryHandlers.Add(new(handler, source));
    }

    /// <summary>Registers a decision hook used before PiSharp loads protected project resources.</summary>
    public void AddProjectTrustHandler(ProjectTrustExtensionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var source = _currentSourceInfo ?? throw new InvalidOperationException(
            "Project trust handlers can only be registered while an extension is being configured.");
        _projectTrustHandlers.Add(new(handler, source));
    }

    internal async Task<ProjectTrustExtensionResolution> ResolveProjectTrustAsync(
        ProjectTrustExtensionContext context, CancellationToken cancellationToken)
    {
        var errors = new List<ProjectTrustExtensionError>();
        foreach (var registration in ProjectTrustHandlers)
        {
            try
            {
                var result = await registration.Handler(context, cancellationToken);
                if (result is null || result.Decision == ProjectTrustExtensionDecision.Undecided) continue;
                return new(result, errors);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                errors.Add(new(registration.SourceInfo.Path, error.Message));
            }
        }
        return new(null, errors);
    }

    public void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler) =>
        AddCommand(name, handler, null);

    public void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler, string? description)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Extension command names must be alphanumeric, '_' or '-'.", nameof(name));
        var sourceInfo = _currentSourceInfo ?? SourceInfoFromHandler(handler);
        if (s_reserved.Contains(name))
            throw new ArgumentException($"Reserved extension command: {name}");
        var replacement = new RegisteredExtensionCommand(name, handler, null, new(description, sourceInfo));
        var ownedCommand = _commands.FindIndex(command => command.Name == name &&
            command.Info.SourceInfo.Path == sourceInfo.Path);
        if (ownedCommand >= 0) _commands[ownedCommand] = replacement;
        else _commands.Add(replacement);
    }

    internal void AddContextCommand(string name,
        PiSharp.Extensions.Abstractions.PiSharpExtensionCommandHandler handler, string? description)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Extension command names must be alphanumeric, '_' or '-'.", nameof(name));
        var sourceInfo = _currentSourceInfo ?? SourceInfoFromHandler(handler);
        if (s_reserved.Contains(name))
            throw new ArgumentException($"Reserved extension command: {name}");
        var replacement = new RegisteredExtensionCommand(name, null, handler, new(description, sourceInfo));
        var ownedCommand = _commands.FindIndex(command => command.Name == name &&
            command.Info.SourceInfo.Path == sourceInfo.Path);
        if (ownedCommand >= 0) _commands[ownedCommand] = replacement;
        else _commands.Add(replacement);
    }

    internal void SetCurrentSourceInfo(ResourceSourceInfo? sourceInfo) => _currentSourceInfo = sourceInfo;

    internal ResourceSourceInfo? CurrentSourceInfo => _currentSourceInfo;

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

    private void AddToolCore(PiSharpToolRegistration definition, PiSharpToolRenderer? renderer)
    {
        var name = definition.Function.Name;
        var assemblyPath = definition.Function.GetType().Assembly.Location;
        var sourceInfo = _currentSourceInfo ??
            (string.IsNullOrWhiteSpace(assemblyPath)
                ? new("<unknown>", "local", "temporary", "top-level", null)
                : new(Path.GetFullPath(assemblyPath), "local", "temporary", "top-level",
                    Path.GetDirectoryName(assemblyPath)));
        if (!_toolCandidates.TryGetValue(name, out var candidates))
        {
            candidates = [];
            _toolCandidates.Add(name, candidates);
        }
        var sameOwner = candidates.FindIndex(candidate => candidate.SourceInfo.Path == sourceInfo.Path);
        if (sameOwner >= 0)
        {
            candidates[sameOwner] = new(definition, sourceInfo, renderer);
            if (sameOwner == 0) SetToolWinner(name, candidates[0]);
            return;
        }
        candidates.Add(new(definition, sourceInfo, renderer));
        if (candidates.Count == 1) SetToolWinner(name, candidates[0]);
    }

    private void SetToolWinner(string name, RegisteredExtensionTool winner)
    {
        _tools[name] = winner.Definition.Function;
        _toolDefinitions[name] = winner.Definition;
        _toolSourceInfo[name] = winner.SourceInfo;
        if (winner.Renderer is null) _toolRenderers.Remove(name);
        else _toolRenderers[name] = winner.Renderer;
    }

    private void RefreshToolWinner(string name)
    {
        if (_toolCandidates.TryGetValue(name, out var candidates) && candidates.Count > 0)
        {
            SetToolWinner(name, candidates[0]);
            return;
        }
        _toolCandidates.Remove(name);
        _tools.Remove(name);
        _toolDefinitions.Remove(name);
        _toolSourceInfo.Remove(name);
        _toolRenderers.Remove(name);
    }

    private (string Name, RegisteredExtensionCommand Command)[] ResolveCommands()
    {
        var counts = _commands.GroupBy(command => command.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        return _commands.Select(command =>
        {
            var occurrence = occurrences.GetValueOrDefault(command.Name) + 1;
            occurrences[command.Name] = occurrence;
            return (counts[command.Name] == 1 ? command.Name : $"{command.Name}:{occurrence}", command);
        }).ToArray();
    }

    internal (string Kind, string Name, string Owner)? FindReplaceableCollision(string builtinPath)
    {
        foreach (var (name, candidates) in _toolCandidates)
        {
            if (!candidates.Any(candidate => candidate.SourceInfo.Path == builtinPath)) continue;
            var owner = candidates.FirstOrDefault(candidate => candidate.SourceInfo.Path != builtinPath &&
                !candidate.SourceInfo.Path.StartsWith("builtin:", StringComparison.Ordinal));
            if (owner is not null) return ("tool", name, owner.SourceInfo.Path);
        }
        foreach (var command in _commands.Where(command => command.Info.SourceInfo.Path == builtinPath))
        {
            var owner = _commands.FirstOrDefault(candidate => candidate.Name == command.Name &&
                candidate.Info.SourceInfo.Path != builtinPath &&
                !candidate.Info.SourceInfo.Path.StartsWith("builtin:", StringComparison.Ordinal));
            if (owner is not null) return ("command", command.Name, owner.Info.SourceInfo.Path);
        }
        return null;
    }

    internal IReadOnlyList<ExtensionConflictDiagnostic> FindToolConflicts()
    {
        var conflicts = new List<ExtensionConflictDiagnostic>();
        foreach (var (name, candidates) in _toolCandidates)
        {
            if (candidates.Count < 2) continue;
            var firstOwner = candidates[0].SourceInfo.Path;
            foreach (var candidate in candidates.Skip(1))
                if (candidate.SourceInfo.Path != firstOwner)
                    conflicts.Add(new(candidate.SourceInfo.Path,
                        $"Tool \"{name}\" conflicts with {firstOwner}"));
        }
        return conflicts;
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
        _userBashHandlers.Add(new(handler, _currentSourceInfo ?? SourceInfoFromHandler(handler)));
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

public sealed record ExtensionLoadError(string Path, string Error);

/// <summary>Loads personal and trust-gated project assemblies, plus caller-selected paths, in isolated contexts for reload.</summary>
public sealed partial class ExtensionCatalog : IDisposable
{
    private readonly List<AssemblyLoadContext> _contexts = [];
    private readonly List<NativeExtensionPackageHandle?> _nativePackageHandles = [];
    private readonly List<IAsyncDisposable> _ownedConnections = [];
    private readonly List<ResourceSourceInfo> _loadedExtensions = [];
    private readonly List<ExtensionLoadError> _loadErrors = [];
    private readonly List<ExtensionConflictDiagnostic> _conflicts = [];
    private readonly List<ExtensionLoadWarning> _warnings = [];
    private readonly HashSet<string> _loadedBuiltins = new(StringComparer.Ordinal);
    private int _disposed;
    public ExtensionRegistration Registration { get; } = new();
    public IReadOnlySet<string> LoadedBuiltins => _loadedBuiltins;
    public IReadOnlyList<ResourceSourceInfo> LoadedExtensions => _loadedExtensions.ToArray();
    internal IReadOnlyList<WeakReference> NativePackageLoadContextReferences => _nativePackageHandles
        .Where(handle => handle is not null).Select(handle => handle!.LoadContextReference).ToArray();
    public IReadOnlyList<ExtensionLoadError> LoadErrors => _loadErrors.ToArray();
    public IReadOnlyList<ExtensionConflictDiagnostic> ConflictDiagnostics => _conflicts.ToArray();
    public IReadOnlyList<ExtensionLoadWarning> Warnings => _warnings.ToArray();

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
        if (_loadedBuiltins.Contains(builtin.Name) || !IsBuiltinEnabled(builtin.Name, userPaths, projectPaths)) return;
        ConfigureBuiltin(builtin);
    }

    public static ExtensionCatalog Load(string agentDirectory, string cwd, bool projectTrusted, bool discover = true,
        IReadOnlyList<string>? additionalPaths = null, IReadOnlyList<string>? userPaths = null,
        IReadOnlyList<string>? projectPaths = null, IReadOnlyList<BuiltinExtensionDefinition>? builtins = null,
        ExtensionCatalog? preloaded = null,
        IReadOnlyList<ResourcePackageSource>? userPackages = null, IReadOnlyList<ResourcePackageSource>? projectPackages = null,
        IReadOnlyList<ManagedResourcePackageRoot>? managedPackageRoots = null,
        IReadOnlyList<NativeExtensionPackageRoot>? nativeExtensionPackageRoots = null)
    {
        var catalog = preloaded ?? new ExtensionCatalog();
        try
        {
            var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var selectedPaths = new List<(string Path, ResourceSourceInfo SourceInfo)>();
            var selectedNativePackages = new Dictionary<string, NativeExtensionPackageRoot>(pathComparer);
            var selectedBuiltins = new List<BuiltinExtensionDefinition>();
            var builtinByName = (builtins ?? []).ToDictionary(builtin => builtin.Name, StringComparer.Ordinal);
            var seenBuiltins = new HashSet<string>(StringComparer.Ordinal);
            var seenPaths = new HashSet<string>(catalog._loadErrors.Select(error => error.Path).Select(ResourcePathIdentity.Canonicalize),
                pathComparer);
            var packageFiles = discover ? ResourcePackageFiles.Load(cwd, agentDirectory, projectTrusted,
                userPackages, projectPackages, CancellationToken.None, managedPackageRoots).Extensions : [];
            var packagePaths = packageFiles.Select(file => file.Path).ToHashSet(seenPaths.Comparer);
            var packageMetadata = packageFiles.ToDictionary(file => file.Path, file => file.Source, seenPaths.Comparer);
            foreach (var entry in additionalPaths ?? [])
            {
                if (entry.StartsWith("builtin:", StringComparison.Ordinal))
                {
                    AddBuiltin(entry);
                    continue;
                }
                var path = ResourceLocalPath.Resolve(entry, cwd);
                IEnumerable<string> paths;
                if (Directory.Exists(path)) paths = FindAssemblies(path);
                else if (File.Exists(path) && Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    paths = [path];
                else
                {
                    if (seenPaths.Add(ResourcePathIdentity.Canonicalize(path))) catalog._loadErrors.Add(new(path, File.Exists(path)
                        ? "Explicit extension must be a .dll file or an existing directory."
                        : "Extension path does not exist: " + path));
                    continue;
                }
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
                            if (!packagePaths.Contains(path)) AddPath(path, new(Path.GetFullPath(path), "auto", "project", "top-level",
                                Path.GetFullPath(Path.Combine(cwd, ".pi"))));
                }
                var userRoot = Path.Combine(agentDirectory, "extensions");
                if (Directory.Exists(userRoot))
                    foreach (var path in LocalResourcePathRules.ApplyOverrides(FindAssemblies(userRoot), userPaths,
                        Path.GetFullPath(agentDirectory)))
                        if (!packagePaths.Contains(path)) AddPath(path, new(Path.GetFullPath(path), "auto", "user", "top-level", Path.GetFullPath(agentDirectory)));
                foreach (var file in packageFiles)
                    if (file.Enabled) AddPath(file.Path, file.Source);
                foreach (var packageRoot in nativeExtensionPackageRoots ?? [])
                {
                    if (packageRoot.Scope == "project" && !projectTrusted)
                    {
                        try
                        {
                            var manifest = NativeExtensionPackageManifestReader.Read(packageRoot.Path);
                            catalog._warnings.Add(new(packageRoot.Path,
                                $"Project native extension {manifest.Id} {manifest.Version} is not loaded because this project is not trusted."));
                        }
                        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                                       ArgumentException or NotSupportedException)
                        {
                            catalog._warnings.Add(new(packageRoot.Path,
                                $"Project native extension metadata could not be read while this project is untrusted: {error.Message}"));
                        }
                        continue;
                    }

                    if (packageRoot.Scope is not ("user" or "project"))
                    {
                        catalog._warnings.Add(new(packageRoot.Path,
                            $"Native extension package has unsupported scope '{packageRoot.Scope}'."));
                        continue;
                    }

                    var packagePath = Path.GetFullPath(packageRoot.Path);
                    var sourceInfo = new ResourceSourceInfo(packagePath, "native-package", packageRoot.Scope,
                        "package", packagePath);
                    if (AddPath(packagePath, sourceInfo))
                        selectedNativePackages[ResourcePathIdentity.Canonicalize(packagePath)] = packageRoot;
                }
            }
            catalog.SelectPreloadedExtensions(selectedPaths);
            var retainedPaths = catalog._loadedExtensions.Select(extension => ResourcePathIdentity.Canonicalize(extension.Path))
                .ToHashSet(seenPaths.Comparer);
            foreach (var (path, sourceInfo) in selectedPaths)
            {
                if (retainedPaths.Contains(ResourcePathIdentity.Canonicalize(path))) continue;
                if (selectedNativePackages.ContainsKey(ResourcePathIdentity.Canonicalize(path)))
                {
                    var loadedNative = NativeExtensionPackageLoader.Load(path, sourceInfo);
                    if (loadedNative.Error is { } nativeError)
                    {
                        catalog._warnings.Add(new(path, nativeError));
                        continue;
                    }

                    var handle = loadedNative.Handle!;
                    var restore = catalog.Registration.CreateConfigurationCheckpoint();
                    catalog.Registration.SetCurrentSourceInfo(sourceInfo);
                    try
                    {
                        foreach (var extension in handle.Extensions)
                        {
                            var builder = new NativeExtensionRegistrationAdapter(catalog.Registration);
                            try { extension.Configure(builder); }
                            finally { builder.Close(); }
                        }
                        handle.ReleaseExtensions();
                        catalog._loadedExtensions.Add(sourceInfo with { Path = path });
                        catalog._contexts.Add(handle.LoadContext!);
                        catalog._nativePackageHandles.Add(handle);
                    }
                    catch (Exception configureError)
                    {
                        restore();
                        handle.Unload();
                        catalog._warnings.Add(new(path,
                            $"Native extension package {handle.Manifest.Id} failed during Configure: {configureError.Message}"));
                    }
                    finally { catalog.Registration.SetCurrentSourceInfo(null); }
                    continue;
                }

                var loaded = ExtensionAssemblyLoader.Load(path, sourceInfo, catalog.Registration);
                if (loaded.Error is { } error) catalog._loadErrors.Add(new(path, error));
                else
                {
                    catalog._loadedExtensions.Add(sourceInfo with { Path = path });
                    catalog._contexts.Add(loaded.Context!);
                    catalog._nativePackageHandles.Add(null);
                }
            }
            catalog.OrderSelectedExtensions(selectedPaths);
            if (discover)
                foreach (var builtin in builtins ?? [])
                    if (builtin.AutoEnable && (builtin.ShouldAutoEnable?.Invoke(catalog.Registration) ?? true) &&
                        IsBuiltinEnabled(builtin.Name, userPaths, projectTrusted ? projectPaths : null))
                        AddBuiltin("builtin:" + builtin.Name);
            foreach (var builtin in selectedBuiltins)
            {
                catalog.ConfigureBuiltin(builtin);
            }
            catalog._conflicts.Clear();
            catalog._conflicts.AddRange(catalog.Registration.FindToolConflicts());
            return catalog;

            void AddBuiltin(string identifier)
            {
                var name = identifier["builtin:".Length..];
                if (!builtinByName.TryGetValue(name, out var builtin))
                    throw new FileNotFoundException($"Unknown built-in extension: {identifier}", identifier);
                if (seenBuiltins.Add(name)) selectedBuiltins.Add(builtin);
            }

            bool AddPath(string path, ResourceSourceInfo sourceInfo)
            {
                var fullPath = Path.GetFullPath(path);
                sourceInfo = packageMetadata.GetValueOrDefault(fullPath, sourceInfo);
                if (!seenPaths.Add(ResourcePathIdentity.Canonicalize(fullPath))) return false;
                selectedPaths.Add((fullPath, sourceInfo with { Path = fullPath }));
                return true;
            }

            void AddConfiguredPaths(IReadOnlyList<string>? entries, string baseDirectory, string scope)
            {
                var fullBase = Path.GetFullPath(baseDirectory);
                var candidates = LocalResourcePathRules.GetPaths(entries)
                    .Where(entry => !entry.StartsWith("builtin:", StringComparison.Ordinal))
                    .SelectMany(entry => EnumerateConfiguredAssemblies(LocalResourcePathRules.ResolvePath(entry, fullBase)))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
                foreach (var path in LocalResourcePathRules.ApplyOverrides(candidates, entries, fullBase))
                    if (!packagePaths.Contains(path)) AddPath(path, new(Path.GetFullPath(path), "local", scope, "top-level", fullBase));
                foreach (var entry in LocalResourcePathRules.GetPaths(entries).Where(entry =>
                    entry.StartsWith("builtin:", StringComparison.Ordinal)))
                    if (IsBuiltinEnabled(entry["builtin:".Length..], userPaths, projectTrusted ? projectPaths : null))
                        AddBuiltin(entry);
            }
        }
        catch { catalog.Dispose(); throw; }
    }

    private void ConfigureBuiltin(BuiltinExtensionDefinition builtin)
    {
        var source = new ResourceSourceInfo("builtin:" + builtin.Name, "builtin", "builtin", "top-level", null);
        var restore = Registration.CreateConfigurationCheckpoint();
        Registration.SetCurrentSourceInfo(source);
        try { builtin.Configure(Registration); }
        finally { Registration.SetCurrentSourceInfo(null); }

        var collision = builtin.Replaceable ? Registration.FindReplaceableCollision(source.Path) : null;
        if (collision is { } replacement)
        {
            restore();
            var registeredName = replacement.Kind switch
            {
                "command" => "/" + replacement.Name,
                "flag" => "--" + replacement.Name,
                _ => replacement.Name
            };
            _warnings.Add(new(source.Path,
                $"Extension {replacement.Owner} registers {replacement.Kind} `{registeredName}`, so built-in extension " +
                $"`{builtin.Name}` was not loaded. To use `{builtin.Name}`, run `pi config` and make sure it is enabled " +
                "under Built-in extensions, then disable or remove the existing extension. We recommend only having " +
                "one or the other loaded at a time."));
            return;
        }
        _loadedBuiltins.Add(builtin.Name);
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

    private static IEnumerable<string> FindAssemblies(string root) => ResourceExtensionFiles.Enumerate(root);

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<Exception>? errors = null;
        try
        {
            foreach (var connection in _ownedConnections)
            {
                try { connection.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
        }
        finally
        {
            _ownedConnections.Clear();
            Registration.ReleaseExtensionReferences();
            for (var index = 0; index < _contexts.Count; index++)
            {
                if (_nativePackageHandles[index] is { } package) package.Unload();
                else _contexts[index].Unload();
            }
            _nativePackageHandles.Clear();
            _contexts.Clear();
        }
        if (errors is { Count: 1 }) throw errors[0];
        if (errors is { Count: > 1 }) throw new AggregateException(errors);
    }

}
