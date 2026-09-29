using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Tools;

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

public sealed class ExtensionRegistration
{
    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal)
    {
        "tree", "branch", "fork", "clone", "new", "sessions", "resume", "name", "model", "models",
        "compact", "export", "export-jsonl", "import", "session", "trust", "reload", "quit", "exit"
    };
    private readonly Dictionary<string, AIFunction> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PiSharpToolRegistration> _toolDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PiSharpToolRenderer> _toolRenderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionCommandInfo> _commandInfo = new(StringComparer.Ordinal);
    private readonly List<UserBashHandler> _userBashHandlers = [];
    private readonly List<PiSharpToolCallHook> _toolCallHooks = [];
    private readonly List<PiSharpToolResultHook> _toolResultHooks = [];
    private ResourceSourceInfo? _currentSourceInfo;
    public IReadOnlyCollection<AIFunction> Tools => _tools.Values;
    public IReadOnlyCollection<PiSharpToolRegistration> ToolDefinitions => _toolDefinitions.Values;
    public IReadOnlyDictionary<string, PiSharpToolRenderer> ToolRenderers => _toolRenderers;
    public IReadOnlyDictionary<string, Func<string, CancellationToken, Task<string>>> Commands => _commands;
    public IReadOnlyDictionary<string, ExtensionCommandInfo> CommandInfo => _commandInfo;
    public IReadOnlyList<UserBashHandler> UserBashHandlers => _userBashHandlers;
    public IReadOnlyList<PiSharpToolCallHook> ToolCallHooks => _toolCallHooks;
    public IReadOnlyList<PiSharpToolResultHook> ToolResultHooks => _toolResultHooks;

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
        if (!_tools.TryAdd(definition.Function.Name, definition.Function))
            throw new ArgumentException($"Duplicate extension tool: {definition.Function.Name}");
        _toolDefinitions.Add(definition.Function.Name, definition);
    }

    /// <summary>Registers an extension tool with optional safe terminal call/result renderers.</summary>
    public void AddTool(AIFunction tool, PiSharpToolRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        AddTool(tool);
        _toolRenderers.Add(tool.Name, renderer);
    }

    /// <summary>Registers a configured extension tool with optional safe terminal renderers.</summary>
    public void AddTool(PiSharpToolRegistration definition, PiSharpToolRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(renderer);
        AddTool(definition);
        _toolRenderers.Add(definition.Function.Name, renderer);
    }

    public PiSharpToolRenderer? GetToolRenderer(string name) =>
        _toolRenderers.TryGetValue(name, out var renderer) ? renderer : null;

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
    public ExtensionRegistration Registration { get; } = new();

    private ExtensionCatalog() { }

    public static ExtensionCatalog Load(string agentDirectory, string cwd, bool projectTrusted, bool discover = true,
        IReadOnlyList<string>? additionalPaths = null, IReadOnlyList<string>? userPaths = null,
        IReadOnlyList<string>? projectPaths = null)
    {
        var catalog = new ExtensionCatalog();
        try
        {
            var selectedPaths = new List<(string Path, ResourceSourceInfo SourceInfo)>();
            var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var entry in additionalPaths ?? [])
            {
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
            return catalog;

            void AddPath(string path, ResourceSourceInfo sourceInfo)
            {
                var fullPath = Path.GetFullPath(path);
                if (seenPaths.Add(fullPath)) selectedPaths.Add((fullPath, sourceInfo with { Path = fullPath }));
            }

            void AddConfiguredPaths(IReadOnlyList<string>? entries, string baseDirectory, string scope)
            {
                var fullBase = Path.GetFullPath(baseDirectory);
                var candidates = LocalResourcePathRules.GetPaths(entries)
                    .SelectMany(entry => EnumerateConfiguredAssemblies(LocalResourcePathRules.ResolvePath(entry, fullBase)))
                    .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
                foreach (var path in LocalResourcePathRules.ApplyOverrides(candidates, entries, fullBase))
                    AddPath(path, new(Path.GetFullPath(path), "local", scope, "top-level", fullBase));
            }
        }
        catch { catalog.Dispose(); throw; }
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
