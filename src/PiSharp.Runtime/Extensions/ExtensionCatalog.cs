using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.AI;
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

public sealed class ExtensionRegistration
{
    private static readonly HashSet<string> s_reserved = new(StringComparer.Ordinal)
    {
        "tree", "branch", "fork", "clone", "new", "sessions", "resume", "name", "model", "models",
        "compact", "export", "export-jsonl", "import", "session", "trust", "reload", "quit", "exit"
    };
    private readonly Dictionary<string, AIFunction> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PiSharpToolRenderer> _toolRenderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> _commands = new(StringComparer.Ordinal);
    private readonly List<UserBashHandler> _userBashHandlers = [];
    public IReadOnlyCollection<AIFunction> Tools => _tools.Values;
    public IReadOnlyDictionary<string, PiSharpToolRenderer> ToolRenderers => _toolRenderers;
    public IReadOnlyDictionary<string, Func<string, CancellationToken, Task<string>>> Commands => _commands;
    public IReadOnlyList<UserBashHandler> UserBashHandlers => _userBashHandlers;

    public void AddTool(AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!_tools.TryAdd(tool.Name, tool)) throw new ArgumentException($"Duplicate extension tool: {tool.Name}");
    }

    /// <summary>Registers an extension tool with optional safe terminal call/result renderers.</summary>
    public void AddTool(AIFunction tool, PiSharpToolRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        AddTool(tool);
        _toolRenderers.Add(tool.Name, renderer);
    }

    public PiSharpToolRenderer? GetToolRenderer(string name) =>
        _toolRenderers.TryGetValue(name, out var renderer) ? renderer : null;

    public void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("Extension command names must be alphanumeric, '_' or '-'.", nameof(name));
        if (s_reserved.Contains(name) || !_commands.TryAdd(name, handler))
            throw new ArgumentException($"Reserved or duplicate extension command: {name}");
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
        IReadOnlyList<string>? additionalPaths = null)
    {
        var catalog = new ExtensionCatalog();
        try
        {
            var selectedPaths = new List<string>();
            foreach (var entry in additionalPaths ?? [])
            {
                var path = Path.GetFullPath(entry, cwd);
                if (Directory.Exists(path)) selectedPaths.AddRange(FindAssemblies(path));
                else if (File.Exists(path) && Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    selectedPaths.Add(path);
                else throw new FileNotFoundException("Explicit extension must be a .dll file or an existing directory.", path);
            }
            if (discover)
                foreach (var root in new[] { Path.Combine(agentDirectory, "extensions"),
                    projectTrusted ? Path.Combine(cwd, ".pi", "extensions") : null }.Where(path => path is not null))
                    if (Directory.Exists(root)) selectedPaths.AddRange(FindAssemblies(root));
            foreach (var path in selectedPaths.Distinct(StringComparer.Ordinal))
            {
                var context = new PluginLoadContext(path);
                catalog._contexts.Add(context);
                var assembly = context.LoadFromAssemblyPath(path);
                foreach (var type in assembly.GetExportedTypes().Where(type =>
                    typeof(IPiSharpExtension).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface))
                {
                    if (Activator.CreateInstance(type) is not IPiSharpExtension extension)
                        throw new InvalidDataException($"Extension {type.FullName} needs a public parameterless constructor.");
                    extension.Configure(catalog.Registration);
                }
            }
            return catalog;
        }
        catch { catalog.Dispose(); throw; }
    }

    private static IEnumerable<string> FindAssemblies(string root) =>
        Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateDirectories(root).Select(path => Path.Combine(path, "index.dll"))
                .Where(File.Exists)).Order(StringComparer.Ordinal).Take(100).Select(Path.GetFullPath).ToArray();

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
