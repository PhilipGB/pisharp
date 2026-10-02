using System.Text.Json;

namespace PiSharp.Runtime.Extensions;

/// <summary>A mutable session selection that publishes immutable snapshots atomically.</summary>
public sealed class ToolLoadout
{
    private readonly PiSharpToolRegistry _registry;
    private readonly object _gate = new();
    private ToolLoadoutSnapshot _snapshot;
    private IReadOnlyList<string> _declarationOrder;
    private readonly HashSet<string> _pendingToolNames = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, JsonElement> _codemodeStore =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    internal ToolLoadout(PiSharpToolRegistry registry, IEnumerable<string> initialActiveNames)
    {
        _registry = registry;
        _snapshot = registry.CreateSnapshot(initialActiveNames);
        _declarationOrder = _snapshot.ActiveToolNames;
        _registry.TrackLoadout(this);
    }

    public ToolLoadoutSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public IReadOnlyDictionary<string, JsonElement> CodemodeStore => Volatile.Read(ref _codemodeStore);

    internal void SetCodemodeStore(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 256 || values.Any(pair => pair.Key.Length > 256 ||
            pair.Value.GetRawText().Length > 256 * 1024) ||
            JsonSerializer.SerializeToUtf8Bytes(values).Length > 1024 * 1024)
            throw new InvalidDataException("Codemode store exceeds its size limit.");
        lock (_gate)
            Volatile.Write(ref _codemodeStore, values.ToDictionary(pair => pair.Key,
                pair => pair.Value.Clone(), StringComparer.Ordinal));
    }

    /// <summary>Replaces the active set, ignoring unknown and hidden tool names.</summary>
    public void SetActiveTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        var names = toolNames.ToArray();
        lock (_gate)
        {
            var next = _registry.CreateSnapshot(names);
            if (_snapshot.ActiveToolNames.Except(next.ActiveToolNames, StringComparer.Ordinal).Any())
                _pendingToolNames.Clear();
            Publish(next);
        }
    }

    internal void RestoreActiveTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        var names = toolNames.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        lock (_gate)
        {
            _pendingToolNames.Clear();
            _pendingToolNames.UnionWith(names);
            _declarationOrder = names.Distinct(StringComparer.Ordinal).ToArray();
            Publish(_registry.CreateSnapshot(names));
        }
    }

    internal IReadOnlyList<string> BeginRun()
    {
        lock (_gate)
        {
            _pendingToolNames.Clear();
            return UpdateDeclarationOrder(_snapshot.ActiveToolNames);
        }
    }

    internal IReadOnlyList<PiSharpToolDeclaration> GetDeclarationsForRequest()
    {
        lock (_gate)
        {
            var declarations = _snapshot.Declared.ToDictionary(tool => tool.Registration.Function.Name,
                StringComparer.Ordinal);
            return UpdateDeclarationOrder(declarations.Keys).Select(name => declarations[name]).ToArray();
        }
    }

    private IReadOnlyList<string> UpdateDeclarationOrder(IEnumerable<string> names)
    {
        var current = names.ToArray();
        var active = current.ToHashSet(StringComparer.Ordinal);
        _declarationOrder = _declarationOrder.Where(active.Contains).Concat(current)
            .Distinct(StringComparer.Ordinal).ToArray();
        return _declarationOrder;
    }

    private void Publish(ToolLoadoutSnapshot snapshot)
    {
        _pendingToolNames.ExceptWith(snapshot.ActiveToolNames);
        Volatile.Write(ref _snapshot, snapshot);
    }

    /// <summary>Add declarations without losing another tool's concurrent loadout change.</summary>
    public void ActivateTools(IEnumerable<string> toolNames)
    {
        ArgumentNullException.ThrowIfNull(toolNames);
        var names = toolNames.ToArray();
        lock (_gate)
        {
            var next = _registry.CreateSnapshot(_snapshot.ActiveToolNames.Concat(names));
            Publish(next);
        }
    }

    internal void RefreshForRegistryChange(IReadOnlyList<PiSharpToolRegistration> previous,
        IReadOnlyList<PiSharpToolRegistration> current)
    {
        var oldByName = previous.ToDictionary(registration => registration.Function.Name, StringComparer.Ordinal);
        lock (_gate)
        {
            var active = _snapshot.ActiveToolNames.ToHashSet(StringComparer.Ordinal);
            var currentByName = current.ToDictionary(registration => registration.Function.Name, StringComparer.Ordinal);
            active.RemoveWhere(name => !currentByName.ContainsKey(name));
            foreach (var registration in current)
            {
                var name = registration.Function.Name;
                var wasDefaultActive = oldByName.TryGetValue(name, out var old) &&
                    PiSharpToolRegistry.IsActiveByDefault(old);
                var isDefaultActive = PiSharpToolRegistry.IsActiveByDefault(registration);
                if (wasDefaultActive && !isDefaultActive) active.Remove(name);
                var becameDefaultActive = isDefaultActive && !wasDefaultActive;
                if (becameDefaultActive) active.Add(name);
            }
            Publish(_registry.CreateSnapshot(active.Concat(_pendingToolNames)));
        }
    }
}
