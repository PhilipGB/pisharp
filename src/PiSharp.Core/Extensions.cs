using System.Reflection;
using System.Runtime.Loader;

namespace PiSharp.Core;

/// <summary>Lifecycle points exposed to trusted PiSharp extensions.</summary>
public enum PiSharpExtensionEvent
{
    /// <summary>Raised before an input is sent to the agent.</summary>
    BeforeTurn,
    /// <summary>Raised after a turn has completed successfully.</summary>
    AfterTurn,
    /// <summary>Raised when a turn is cancelled.</summary>
    TurnCancelled,
    /// <summary>Raised when the host is shutting down.</summary>
    Shutdown,
}

/// <summary>Context supplied to extension handlers.</summary>
public sealed record PiSharpExtensionContext(
    string WorkspaceRoot,
    TurnMessageQueue TurnQueue,
    CancellationToken CancellationToken,
    Action<string>? Notify = null);

/// <summary>Result returned by an extension command.</summary>
public sealed record PiSharpCommandResult(bool Handled, string? Message = null);

/// <summary>A command registered by an extension.</summary>
public sealed record PiSharpCommand(
    string Name,
    string Description,
    Func<string?, PiSharpExtensionContext, Task<PiSharpCommandResult>> Handler);

/// <summary>Diagnostics produced while loading or invoking extensions.</summary>
public sealed record ExtensionDiagnostic(string Message, string? Path = null);

/// <summary>Result of loading extension assemblies.</summary>
public sealed record ExtensionLoadResult(
    IReadOnlyList<string> LoadedExtensions,
    IReadOnlyList<ExtensionDiagnostic> Diagnostics);

/// <summary>Contract implemented by a trusted PiSharp extension assembly.</summary>
public interface IPiSharpExtension
{
    /// <summary>Gets the stable display name of the extension.</summary>
    string Name { get; }

    /// <summary>Registers commands, input transforms, and lifecycle handlers.</summary>
    void Configure(PiSharpExtensionRegistry registry);
}

/// <summary>Registration surface made available while an extension is configured.</summary>
public sealed class PiSharpExtensionRegistry
{
    private readonly Dictionary<string, PiSharpCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PiSharpExtensionEvent, List<Func<PiSharpExtensionContext, Task>>> _handlers = [];
    private readonly List<Func<string, string>> _inputTransforms = [];

    /// <summary>Registers or replaces a slash command.</summary>
    public void RegisterCommand(PiSharpCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommandName(command.Name);
        _commands[command.Name] = command;
    }

    /// <summary>Registers an asynchronous lifecycle handler.</summary>
    public void On(PiSharpExtensionEvent extensionEvent, Func<PiSharpExtensionContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryGetValue(extensionEvent, out var handlers))
        {
            handlers = [];
            _handlers[extensionEvent] = handlers;
        }
        handlers.Add(handler);
    }

    /// <summary>Registers a deterministic input transformation.</summary>
    public void RegisterInputTransform(Func<string, string> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        _inputTransforms.Add(transform);
    }

    internal IReadOnlyDictionary<string, PiSharpCommand> Commands => _commands;

    internal string TransformInput(string input)
    {
        return _inputTransforms.Aggregate(input, (current, transform) => transform(current));
    }

    internal async Task PublishAsync(
        PiSharpExtensionEvent extensionEvent,
        PiSharpExtensionContext context,
        ICollection<ExtensionDiagnostic> diagnostics)
    {
        if (!_handlers.TryGetValue(extensionEvent, out var handlers))
        {
            return;
        }
        foreach (var handler in handlers.ToArray())
        {
            try
            {
                await handler(context);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new ExtensionDiagnostic(exception.Message));
            }
        }
    }

    private static void ValidateCommandName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains(' ', StringComparison.Ordinal) || name.StartsWith('/'))
        {
            throw new ArgumentException("Extension command names must be non-empty slash-command names without spaces.", nameof(name));
        }
    }
}

/// <summary>
/// Hosts trusted .NET extensions. Assemblies are loaded only from explicitly
/// configured directories; loading an assembly executes its trusted code.
/// </summary>
public sealed class PiSharpExtensionHost
{
    private readonly PiSharpExtensionRegistry _registry = new();
    private readonly List<IPiSharpExtension> _extensions = [];
    private readonly List<string> _loadedPaths = [];
    private readonly List<ExtensionDiagnostic> _diagnostics = [];

    /// <summary>Gets registered extension commands.</summary>
    public IReadOnlyCollection<PiSharpCommand> Commands => _registry.Commands.Values.ToArray();

    /// <summary>Gets diagnostics accumulated while loading and invoking extensions.</summary>
    public IReadOnlyList<ExtensionDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Gets assembly paths that were actually loaded into the process.</summary>
    public IReadOnlyList<string> LoadedPaths => _loadedPaths;

    /// <summary>Registers an in-process extension instance.</summary>
    public void Register(IPiSharpExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        try
        {
            extension.Configure(_registry);
            _extensions.Add(extension);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Add(new ExtensionDiagnostic(exception.Message));
        }
    }

    /// <summary>Loads all compatible extension types from explicit DLL paths or directories.</summary>
    public ExtensionLoadResult LoadFromPaths(IEnumerable<string> paths)
    {
        var loaded = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            LoadPath(path, loaded);
        }
        return new ExtensionLoadResult(loaded, _diagnostics.ToArray());
    }

    /// <summary>Transforms user input through extensions in registration order.</summary>
    public string TransformInput(string input) => _registry.TransformInput(input);

    /// <summary>Runs a registered slash command, or returns false when none matches.</summary>
    public async Task<PiSharpCommandResult> ExecuteCommandAsync(
        string input,
        PiSharpExtensionContext context)
    {
        var (name, arguments) = ParseCommand(input);
        if (name is null || !_registry.Commands.TryGetValue(name, out var command))
        {
            return new PiSharpCommandResult(false);
        }
        try
        {
            return await command.Handler(arguments, context);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Add(new ExtensionDiagnostic($"Command /{name} failed: {exception.Message}"));
            return new PiSharpCommandResult(true, $"Extension command /{name} failed: {exception.Message}");
        }
    }

    /// <summary>Publishes a lifecycle event to all registered handlers.</summary>
    public Task PublishAsync(PiSharpExtensionEvent extensionEvent, PiSharpExtensionContext context) =>
        _registry.PublishAsync(extensionEvent, context, _diagnostics);

    private void LoadPath(string path, ICollection<string> loaded)
    {
        if (File.Exists(path))
        {
            LoadAssembly(path, loaded);
            return;
        }
        if (Directory.Exists(path))
        {
            LoadDirectory(path, loaded);
        }
    }

    private void LoadDirectory(string directory, ICollection<string> loaded)
    {
        if (!Directory.Exists(directory))
        {
            _diagnostics.Add(new ExtensionDiagnostic("extension directory does not exist", directory));
            return;
        }

        IEnumerable<string> assemblies;
        try
        {
            assemblies = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal);
        }
        catch (IOException exception)
        {
            _diagnostics.Add(new ExtensionDiagnostic(exception.Message, directory));
            return;
        }
        catch (UnauthorizedAccessException exception)
        {
            _diagnostics.Add(new ExtensionDiagnostic(exception.Message, directory));
            return;
        }

        foreach (var path in assemblies)
        {
            LoadAssembly(path, loaded);
        }
    }

    private void LoadAssembly(string path, ICollection<string> loaded)
    {
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
            var types = assembly.GetTypes()
                .Where(type => typeof(IPiSharpExtension).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface);
            var loadedAny = false;
            foreach (var type in types)
            {
                if (Activator.CreateInstance(type) is not IPiSharpExtension extension)
                {
                    continue;
                }
                Register(extension);
                loaded.Add(extension.Name);
                loadedAny = true;
            }
            if (loadedAny)
            {
                _loadedPaths.Add(Path.GetFullPath(path));
            }
        }
        catch (ReflectionTypeLoadException exception)
        {
            _diagnostics.Add(new ExtensionDiagnostic(
                string.Join("; ", exception.LoaderExceptions.OfType<Exception>().Select(error => error.Message)), path));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _diagnostics.Add(new ExtensionDiagnostic(exception.Message, path));
        }
    }

    private static (string? Name, string? Arguments) ParseCommand(string input)
    {
        if (!input.StartsWith('/'))
        {
            return (null, null);
        }
        var command = input[1..].Split([' ', '\t', '\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries);
        return command.Length == 0 ? (null, null) : (command[0], command.Length == 1 ? null : command[1]);
    }
}
