using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Extensions;

/// <summary>Controls how a registered tool is exposed to the model and other tools.</summary>
public enum ToolExposure
{
    Direct,
    ModelOnly,
    CodeMode,
    Deferred,
    Hidden
}

/// <summary>A named group of related tools, with optional discovery metadata such as MCP instructions.</summary>
public sealed record PiSharpToolNamespace(string Name, string? Description = null, string? Instructions = null);

/// <summary>A function and the policy that governs its exposure in a session.</summary>
public sealed record PiSharpToolRegistration(
    AIFunction Function,
    ToolExposure Exposure = ToolExposure.Direct,
    bool? DefaultActive = null,
    PiSharpToolNamespace? Namespace = null,
    Func<ToolLoadoutSnapshot, ToolLoadoutChanges?>? PrepareLoadout = null,
    bool AllowNestedInvocation = true,
    System.Text.Json.JsonElement? OutputSchema = null,
    string? PromptSnippet = null,
    IReadOnlyList<string>? PromptGuidelines = null);

/// <summary>Changes an active loadout's model-facing declaration projection.</summary>
public sealed record ToolLoadoutChanges(
    IReadOnlyDictionary<string, string>? Descriptions = null,
    IReadOnlyCollection<string>? HiddenDeclarations = null);

/// <summary>A tool declaration and its current model-facing description.</summary>
public sealed record PiSharpToolDeclaration(PiSharpToolRegistration Registration, string Description);

/// <summary>Session-specific controls available to an invoked extension tool.</summary>
internal sealed class DescribedAIFunction(AIFunction inner, string description) : DelegatingAIFunction(inner)
{
    public override string Description => description;
}

/// <summary>An immutable view of registered, callable, active, and declared tools.</summary>
public sealed class ToolLoadoutSnapshot
{
    private readonly IReadOnlyDictionary<string, PiSharpToolRegistration> _byName;

    internal ToolLoadoutSnapshot(
        IReadOnlyList<PiSharpToolRegistration> registered,
        IReadOnlyList<PiSharpToolRegistration> callable,
        IReadOnlyList<PiSharpToolDeclaration> declared,
        IReadOnlyList<string> activeToolNames)
    {
        Registered = Array.AsReadOnly(registered.ToArray());
        Callable = Array.AsReadOnly(callable.ToArray());
        Declared = Array.AsReadOnly(declared.ToArray());
        ActiveToolNames = Array.AsReadOnly(activeToolNames.ToArray());
        _byName = new System.Collections.ObjectModel.ReadOnlyDictionary<string, PiSharpToolRegistration>(
            Registered.ToDictionary(tool => tool.Function.Name, StringComparer.Ordinal));
    }

    /// <summary>Every registered tool, including tools with hidden exposure.</summary>
    public IReadOnlyList<PiSharpToolRegistration> Registered { get; }

    /// <summary>Tools callable through the runtime tool-execution context.</summary>
    public IReadOnlyList<PiSharpToolRegistration> Callable { get; }

    /// <summary>Declarations included in the current model request.</summary>
    public IReadOnlyList<PiSharpToolDeclaration> Declared { get; }

    /// <summary>The active tool set before declaration-only projections are applied.</summary>
    public IReadOnlyList<string> ActiveToolNames { get; }

    public ToolExposure GetExposure(string name) =>
        _byName.TryGetValue(name, out var registration) ? registration.Exposure : ToolExposure.Direct;

    public PiSharpToolNamespace? GetNamespace(string name) =>
        _byName.TryGetValue(name, out var registration) ? registration.Namespace : null;
}

/// <summary>Creates session loadouts over a stable set of registered tools.</summary>
public sealed class PiSharpToolRegistry
{
    private sealed record RegistryState(IReadOnlyList<PiSharpToolRegistration> Registrations,
        IReadOnlyDictionary<string, PiSharpToolRegistration> ByName);

    private RegistryState _state;
    private readonly object _loadoutsGate = new();
    private readonly List<WeakReference<ToolLoadout>> _loadouts = [];

    public PiSharpToolRegistry(IEnumerable<PiSharpToolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _state = CreateState(registrations);
    }

    private static RegistryState CreateState(IEnumerable<PiSharpToolRegistration> registrations)
    {
        var materialized = registrations.ToArray();
        var byName = new Dictionary<string, PiSharpToolRegistration>(StringComparer.Ordinal);
        foreach (var registration in materialized)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentNullException.ThrowIfNull(registration.Function);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.Function.Name);
            if (!Enum.IsDefined(registration.Exposure))
                throw new ArgumentOutOfRangeException(nameof(registrations), registration.Exposure,
                    "Tool exposure must be a defined value.");
            if (registration.Namespace is { } toolNamespace)
                ArgumentException.ThrowIfNullOrWhiteSpace(toolNamespace.Name);
            if (!byName.TryAdd(registration.Function.Name, registration))
                throw new ArgumentException($"Duplicate tool name: {registration.Function.Name}", nameof(registrations));
        }

        return new(Array.AsReadOnly(materialized),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, PiSharpToolRegistration>(byName));
    }

    public IReadOnlyList<PiSharpToolRegistration> Registered => Volatile.Read(ref _state).Registrations;

    internal void Replace(IEnumerable<PiSharpToolRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var next = CreateState(registrations);
        lock (_loadoutsGate)
        {
            var previous = _state;
            Volatile.Write(ref _state, next);
            for (var index = _loadouts.Count - 1; index >= 0; index--)
            {
                if (_loadouts[index].TryGetTarget(out var loadout))
                    loadout.RefreshForRegistryChange(previous.Registrations, next.Registrations);
                else _loadouts.RemoveAt(index);
            }
        }
    }

    internal void TrackLoadout(ToolLoadout loadout)
    {
        lock (_loadoutsGate)
        {
            _loadouts.Add(new WeakReference<ToolLoadout>(loadout));
            loadout.RefreshForRegistryChange(loadout.Snapshot.Registered, _state.Registrations);
        }
    }

    public ToolLoadout CreateLoadout(IEnumerable<string>? activeToolNames = null)
    {
        var registrations = Volatile.Read(ref _state).Registrations;
        var active = activeToolNames is null
            ? registrations.Where(IsActiveByDefault).Select(registration => registration.Function.Name)
            : activeToolNames;
        return new ToolLoadout(this, active);
    }

    internal ToolLoadoutSnapshot CreateSnapshot(IEnumerable<string> requestedActiveNames)
    {
        var state = Volatile.Read(ref _state);
        var activeNames = new List<string>();
        var activeSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in requestedActiveNames)
        {
            if (string.IsNullOrWhiteSpace(name) || !state.ByName.TryGetValue(name, out var registration)) continue;
            if (registration.Exposure == ToolExposure.Hidden || !activeSet.Add(name)) continue;
            activeNames.Add(name);
        }

        var declared = activeNames.Select(name => state.ByName[name]).ToArray();
        var callable = state.Registrations.Where(registration => registration.Exposure switch
        {
            ToolExposure.Direct => registration.AllowNestedInvocation && activeSet.Contains(registration.Function.Name),
            ToolExposure.CodeMode or ToolExposure.Deferred => registration.AllowNestedInvocation,
            _ => false
        }).ToArray();
        var initial = new ToolLoadoutSnapshot(state.Registrations.ToArray(), callable, declared.Select(registration =>
            new PiSharpToolDeclaration(registration, registration.Function.Description ?? string.Empty)).ToArray(), activeNames);

        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var hiddenDeclarations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in declared)
        {
            var changes = registration.PrepareLoadout?.Invoke(initial);
            if (changes is null) continue;
            if (changes.Descriptions is not null)
                foreach (var (name, description) in changes.Descriptions)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(name);
                    ArgumentNullException.ThrowIfNull(description);
                    descriptions[name] = description;
                }
            if (changes.HiddenDeclarations is not null)
                foreach (var name in changes.HiddenDeclarations)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(name);
                    hiddenDeclarations.Add(name);
                }
        }

        var projectedDeclarations = initial.Declared
            .Where(declaration => !hiddenDeclarations.Contains(declaration.Registration.Function.Name))
            .Select(declaration => descriptions.TryGetValue(declaration.Registration.Function.Name, out var description)
                ? declaration with { Description = description }
                : declaration)
            .ToArray();
        return new ToolLoadoutSnapshot(state.Registrations.ToArray(), callable, projectedDeclarations, activeNames);
    }

    internal static bool IsActiveByDefault(PiSharpToolRegistration registration) =>
        (registration.Exposure is ToolExposure.Direct or ToolExposure.ModelOnly) && registration.DefaultActive != false;
}
