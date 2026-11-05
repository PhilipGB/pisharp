using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli.Tui;

/// <summary>Edits the session model scope through the shared searchable selector list.</summary>
/// <remarks>Mirrors pinned Pi's ScopedModelsSelectorComponent: null enabled ids mean "all enabled";
/// changes apply to the session only until Ctrl+S persists them.</remarks>
internal sealed class TerminalScopedModelsSelector(ProviderModelRuntime modelRuntime, TerminalEditor editor,
    Func<string?, Task> persist, Func<IReadOnlyList<string>?>? configuredEnabledModels = null)
{
    public async Task ShowAsync(IReadOnlyList<ModelDescriptor> catalog, CancellationToken cancellationToken = default)
    {
        var knownModels = new Dictionary<string, ModelDescriptor>(StringComparer.Ordinal);
        var models = RebuildCatalog(catalog);
        var allIds = models.Select(model => Key(model.Provider!, model.Id)).ToList();
        var enabledIds = InitialEnabledIds(models);
        // Pi starts on the first row; the enabled order already places the scoped model first.
        string? selectedKey = null;
        var selectionChanged = false;
        var selectionMoved = false;
        var isDirty = false;
        var options = BuildOptions();

        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var refreshTask = modelRuntime.ListModelsAsync(cancellationToken: refreshCancellation.Token,
            includeOutOfScope: true);
        var refreshApplied = false;
        try
        {
            await editor.ShowSelectionListAsync("Scoped Models", options, selectedKey,
                actionHint: InitialActionHint(),
                handleInput: HandleInput, cancellationToken: cancellationToken,
                onIdle: OnIdleAsync).ConfigureAwait(false);
        }
        finally
        {
            refreshCancellation.Cancel();
            try { await refreshTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        async Task OnIdleAsync(TerminalSelectionList<ModelDescriptor> list)
        {
            // The search query is edited by the shared list, so rebuild the hint on every idle turn.
            SetActionHint(list);
            if (refreshApplied || !refreshTask.IsCompleted) return;
            refreshApplied = true;
            try
            {
                await refreshTask.ConfigureAwait(false);
                models = RebuildCatalog(modelRuntime.GetAvailableModelSnapshot());
                allIds.Clear();
                allIds.AddRange(models.Select(model => Key(model.Provider!, model.Id)));
                // Pi re-derives the configured/session selection from the refreshed catalogue
                // until the user edits it, so a partial startup snapshot cannot freeze the order.
                if (!selectionChanged) enabledIds = InitialEnabledIds(models);
                list.ReplaceOptions(BuildOptions(), null);
                // Pi starts on the first row; a partial opening snapshot must not pin a later refetch.
                if (!selectionChanged && !selectionMoved) list.SelectIndex(0);
                ApplySessionScope();
            }
            catch (OperationCanceledException) when (refreshCancellation.IsCancellationRequested)
            {
                list.SetTitle("Scoped Models · catalog refresh timed out");
            }
            catch (Exception)
            {
                list.SetTitle("Scoped Models · catalog refresh failed");
            }
        }

        async Task<bool> HandleInput(TerminalSelectionList<ModelDescriptor> list, TerminalInputEvent input)
        {
            if (input.Key is not null || input.Text is { Length: > 0 }) selectionMoved = true;
            // Ctrl+C clears an active search before it closes the navigator.
            if (input.Key is { } cancelKey && editor.MatchesKeybinding("tui.select.cancel", cancelKey) &&
                cancelKey.Key == ConsoleKey.C && cancelKey.Modifiers.HasFlag(ConsoleModifiers.Control) &&
                list.SearchQuery.Length > 0)
            {
                list.ClearSearch();
                SetActionHint(list);
                return true;
            }
            if (input.Key is not { } key) return false;
            if (key.Key == ConsoleKey.Enter || editor.MatchesKeybinding("tui.select.confirm", key))
            {
                if (list.Selected is { } current)
                    enabledIds = Toggle(enabledIds, allIds, current.Option.Key);
                selectionChanged = true;
                isDirty = true;
                // Pi keeps the row index across toggles even when the sort order changes.
                list.ReplaceOptions(BuildOptions(), null, preserveIndex: true);
                ApplySessionScope();
                SetActionHint(list);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.enableAll", key))
            {
                enabledIds = EnableAll(enabledIds, allIds, SearchTargets(list));
                selectionChanged = true;
                isDirty = true;
                list.ReplaceOptions(BuildOptions(), null, preserveIndex: true);
                ApplySessionScope();
                SetActionHint(list);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.clearAll", key))
            {
                enabledIds = ClearAll(enabledIds, allIds, SearchTargets(list));
                selectionChanged = true;
                isDirty = true;
                list.ReplaceOptions(BuildOptions(), null, preserveIndex: true);
                ApplySessionScope();
                SetActionHint(list);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.toggleProvider", key))
            {
                if (list.Selected is { } current && current.Option.Value.Available)
                {
                    var provider = current.Option.Value.Provider;
                    var providerIds = models.Where(model => model.Available &&
                            provider!.Equals(model.Provider, StringComparison.Ordinal))
                        .Select(model => Key(model.Provider!, model.Id)).ToArray();
                    enabledIds = providerIds.All(id => IsEnabled(enabledIds, id))
                        ? ClearAll(enabledIds, allIds, providerIds)
                        : EnableAll(enabledIds, allIds, providerIds);
                    selectionChanged = true;
                    isDirty = true;
                    list.ReplaceOptions(BuildOptions(), null, preserveIndex: true);
                    ApplySessionScope();
                }
                SetActionHint(list);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.reorderUp", key) ||
                editor.MatchesKeybinding("app.models.reorderDown", key))
            {
                var reorderUp = editor.MatchesKeybinding("app.models.reorderUp", key);
                var selectedId = list.Selected?.Option.Key;
                if (enabledIds is not null && selectedId is not null && IsEnabled(enabledIds, selectedId))
                {
                    var delta = reorderUp ? -1 : 1;
                    var currentIndex = enabledIds.IndexOf(selectedId);
                    var newIndex = currentIndex + delta;
                    if (newIndex >= 0 && newIndex < enabledIds.Count)
                    {
                        enabledIds = Move(enabledIds, selectedId, delta);
                        selectionChanged = true;
                        isDirty = true;
                        var index = list.SelectedIndex;
                        list.ReplaceOptions(BuildOptions(), null);
                        list.SelectIndex(index + delta);
                        ApplySessionScope();
                    }
                }
                SetActionHint(list);
                return true;
            }
            if (IsSaveKey(editor.MatchesKeybinding, key))
            {
                // A selection covering every available model persists as "all models enabled" (key removed).
                var availableIds = AvailableIds();
                var allAvailableEnabled = enabledIds is not null &&
                    enabledIds.Count == availableIds.Count && enabledIds.All(availableIds.Contains);
                var value = enabledIds is null || allAvailableEnabled
                    ? null : JsonSerializer.Serialize(enabledIds);
                await persist(value).ConfigureAwait(false);
                selectionChanged = true;
                isDirty = false;
                ApplySessionScope();
                SetActionHint(list);
                return true;
            }
            return false;
        }

        IReadOnlyList<string>? SearchTargets(TerminalSelectionList<ModelDescriptor> list) =>
            list.SearchQuery.Length == 0 ? null : list.VisibleOptions.Select(option => option.Key).ToArray();

        /// <summary>Rebuilds the available catalog and keeps prior descriptors for labels and enabled ids.</summary>
        List<ModelDescriptor> RebuildCatalog(IEnumerable<ModelDescriptor> catalog)
        {
            var available = NormalizeCatalog(catalog.Where(model => model.Available));
            UpdateKnownModels(knownModels, available);
            return available;
        }

        ModelDescriptor ResolveModel(string id)
        {
            if (knownModels.TryGetValue(id, out var known)) return known;
            var slash = id.IndexOf('/');
            var provider = slash > 0 ? id[..slash] : "unknown";
            var modelId = slash > 0 ? id[(slash + 1)..] : id;
            var synthesized = new ModelDescriptor(modelId, null, null, "unavailable", Provider: provider,
                Available: false, UnavailableReason: "not present in the current catalogue");
            knownModels[id] = synthesized;
            return synthesized;
        }

        IReadOnlyList<TerminalSelectionOption<ModelDescriptor>> BuildOptions()
        {
            var orderedIds = GetSortedIds(enabledIds, allIds);
            return orderedIds.Select(id =>
            {
                var model = ResolveModel(id);
                var enabled = IsEnabled(enabledIds, id);
                var provider = model.Provider!;
                var label = $"{(enabled ? "✓ " : "  ")}{model.Id} [{provider}]";
                var availability = model.Available ? model.Status ?? "available" : model.UnavailableReason ?? "unavailable";
                return new TerminalSelectionOption<ModelDescriptor>(id, model, label,
                    $"{model.Name ?? model.Id} · {availability}", $"{provider} {provider}/{model.Id} {model.Name}");
            }).ToArray();
        }

        string BuildActionHint()
        {
            var count = EnabledCount(out var unavailableCount, out var allEnabled);
            var countText = allEnabled
                ? "all enabled"
                : $"{count}/{allIds.Count} enabled{(unavailableCount > 0 ? $" · {unavailableCount} unavailable" : "")}";
            var parts = new[]
            {
                // Keep the count and unsaved marker first so narrow terminals retain them before the hint clips.
                countText + (isDirty ? " (unsaved)" : ""),
                editor.GetDisplayKeys("tui.select.confirm") + " toggle",
                editor.GetDisplayKeys("app.models.enableAll") + " all",
                editor.GetDisplayKeys("app.models.clearAll") + " clear",
                editor.GetDisplayKeys("app.models.toggleProvider") + " provider",
                CombinedReorderHint(),
                editor.GetDisplayKeys("app.models.save") + " save"
            };
            return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        int EnabledCount(out int unavailableCount, out bool allEnabled)
        {
            var availableIds = AvailableIds();
            if (enabledIds is null)
            {
                (unavailableCount, allEnabled) = (0, true);
                return availableIds.Count;
            }
            unavailableCount = enabledIds.Count(id => !availableIds.Contains(id));
            allEnabled = false;
            return enabledIds.Count - unavailableCount;
        }

        string? CombinedReorderHint()
        {
            var up = editor.GetDisplayKeys("app.models.reorderUp");
            var down = editor.GetDisplayKeys("app.models.reorderDown");
            if (up is null && down is null) return null;
            if (up is { Length: > 0 } upKeys && down is { Length: > 0 } downKeys && upKeys[^1] == downKeys[^1])
                return $"{upKeys[..^1]}{upKeys[^1]}/{downKeys[^1]} reorder";
            return $"{up}/{down} reorder".TrimStart('/').Trim();
        }

        void SetActionHint(TerminalSelectionList<ModelDescriptor> list)
        {
            var hint = BuildActionHint();
            if (hint.Length > 0) list.SetActionHint(hint);
        }

        string InitialActionHint()
        {
            var hint = BuildActionHint();
            return hint.Length > 0 ? hint : "Entering toggle · Ctrl+S save";
        }

        HashSet<string> AvailableIds() => models.Select(model => Key(model.Provider!, model.Id))
            .ToHashSet(StringComparer.Ordinal);

        void ApplySessionScope()
        {
            // Session scope binds only when at least one enabled model is available and not every
            // available model is enabled; otherwise the session runs on the full catalog.
            if (enabledIds is not null)
            {
                var availableIds = AvailableIds();
                var hasEnabledAvailableModel = enabledIds.Any(availableIds.Contains);
                var allAvailableModelsEnabled = availableIds.All(enabledIds.Contains);
                if (hasEnabledAvailableModel && !allAvailableModelsEnabled)
                {
                    modelRuntime.SetScope(enabledIds);
                    return;
                }
            }
            modelRuntime.ClearScope();
        }
    }

    List<string>? InitialEnabledIds(List<ModelDescriptor> available)
    {
        var configured = configuredEnabledModels?.Invoke();
        var patterns = modelRuntime.HasScope ? modelRuntime.Scope : configured;
        if (patterns is not { Count: > 0 }) return null;
        var result = ModelScopeResolver.Resolve(patterns, available).Models
            .Where(item => item.Model.Provider is not null)
            .Select(item => Key(item.Model.Provider!, item.Model.Id))
            .Distinct(StringComparer.Ordinal).ToList();
        foreach (var pattern in patterns)
        {
            if (pattern.Contains('/') && !pattern.Any(character => character is '*' or '?' or '[' or '{') &&
                !result.Contains(pattern, StringComparer.Ordinal))
            {
                var slash = pattern.IndexOf('/');
                var provider = pattern[..slash];
                var id = pattern[(slash + 1)..];
                if (provider.Length > 0 && id.Length > 0) result.Add(Key(provider, id));
            }
        }
        return result;
    }

    /// <summary>Merges a refreshed snapshot and marks disappeared models unavailable.</summary>
    internal static void UpdateKnownModels(Dictionary<string, ModelDescriptor> known,
        IReadOnlyList<ModelDescriptor> available)
    {
        var current = available.Select(model => Key(model.Provider!, model.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var key in known.Keys.Where(key => !current.Contains(key)).ToArray())
            known[key] = known[key] with
            {
                Available = false,
                Status = null,
                UnavailableReason = "not present in the current catalogue"
            };
        foreach (var model in available)
            known[Key(model.Provider!, model.Id)] = model;
    }

    internal static bool IsSaveKey(Func<string, ConsoleKeyInfo, bool> matches, ConsoleKeyInfo key) =>
        matches("app.models.save", key);

    internal static bool IsEnabled(IReadOnlyList<string>? enabledIds, string id) =>
        enabledIds is null || enabledIds.Contains(id);

    /// <summary>Collapses an explicit list back to null (= all enabled) when it covers every model.</summary>
    internal static List<string>? NormalizeEnabled(List<string> result, List<string> allIds) =>
        result.Count == allIds.Count && result.All(id => allIds.Contains(id)) ? null : result;

    internal static List<string>? Toggle(List<string>? enabledIds, List<string> allIds, string id)
    {
        if (enabledIds is null) return allIds.Where(modelId => !modelId.Equals(id, StringComparison.Ordinal)).ToList();
        var index = enabledIds.FindIndex(item => item.Equals(id, StringComparison.Ordinal));
        if (index >= 0) return [.. enabledIds.Take(index), .. enabledIds.Skip(index + 1)];
        return NormalizeEnabled([.. enabledIds, id], allIds);
    }

    internal static List<string>? EnableAll(List<string>? enabledIds, List<string> allIds,
        IReadOnlyList<string>? targetIds = null)
    {
        if (enabledIds is null) return null;
        var targets = targetIds ?? allIds;
        var result = new List<string>(enabledIds);
        foreach (var id in targets)
            if (!result.Contains(id)) result.Add(id);
        return NormalizeEnabled(result, allIds);
    }

    internal static List<string>? ClearAll(List<string>? enabledIds, List<string> allIds,
        IReadOnlyList<string>? targetIds = null)
    {
        if (enabledIds is null)
            return targetIds is null ? [] : allIds.Where(id => !targetIds.Contains(id)).ToList();
        var targets = targetIds ?? enabledIds;
        return enabledIds.Where(id => !targets.Contains(id)).ToList();
    }

    internal static List<string>? Move(List<string>? enabledIds, string id, int delta)
    {
        if (enabledIds is null) return null;
        var list = new List<string>(enabledIds);
        var index = list.IndexOf(id);
        if (index < 0) return list;
        var newIndex = index + delta;
        if (newIndex < 0 || newIndex >= list.Count) return list;
        (list[index], list[newIndex]) = (list[newIndex], list[index]);
        return list;
    }

    internal static List<string> GetSortedIds(List<string>? enabledIds, List<string> allIds)
    {
        if (enabledIds is null) return allIds;
        var enabledSet = new HashSet<string>(enabledIds, StringComparer.Ordinal);
        return [.. enabledIds, .. allIds.Where(id => !enabledSet.Contains(id))];
    }

    private static List<ModelDescriptor> NormalizeCatalog(IEnumerable<ModelDescriptor> catalog) => catalog
        .Select(model => model.Provider is null ? model with { Provider = "unknown" } : model)
        .GroupBy(model => Key(model.Provider!, model.Id), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToList();

    private static string Key(string provider, string id) => $"{provider}/{id}";
}
