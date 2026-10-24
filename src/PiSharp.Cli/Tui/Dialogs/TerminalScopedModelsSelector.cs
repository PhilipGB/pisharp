using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli.Tui;

/// <summary>Edits the session model scope through the shared searchable selector list.</summary>
internal sealed class TerminalScopedModelsSelector(ProviderModelRuntime modelRuntime, TerminalEditor editor,
    Func<string?, Task> persist)
{
    public async Task ShowAsync(IReadOnlyList<ModelDescriptor> catalog, CancellationToken cancellationToken = default)
    {
        var models = NormalizeCatalog(catalog);
        var allIds = models.Select(model => Key(model.Provider!, model.Id)).ToList();
        var enabledIds = InitialEnabledIds(models);
        var selectedKey = modelRuntime.HasScope ? enabledIds?.FirstOrDefault() : null;
        var options = BuildOptions();

        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        var refreshTask = modelRuntime.ListModelsAsync(cancellationToken: refreshCancellation.Token,
            includeOutOfScope: true);
        var refreshApplied = false;
        try
        {
            await editor.ShowSelectionListAsync("Scoped Models", options, selectedKey,
                actionHint: "Enter toggle · Ctrl+A enable all · Ctrl+X clear · Ctrl+P provider · Alt+↑/↓ reorder · Ctrl+S save · Esc cancel",
                handleInput: HandleInput, cancellationToken: cancellationToken,
                onIdle: RefreshCatalogAsync).ConfigureAwait(false);
        }
        finally
        {
            refreshCancellation.Cancel();
            try { await refreshTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        async Task RefreshCatalogAsync(TerminalSelectionList<ModelDescriptor> list)
        {
            if (refreshApplied || !refreshTask.IsCompleted) return;
            refreshApplied = true;
            try
            {
                var refreshed = await refreshTask.ConfigureAwait(false);
                models = NormalizeCatalog(refreshed.Concat(models));
                allIds = models.Select(model => Key(model.Provider!, model.Id)).ToList();
                list.ReplaceOptions(BuildOptions(), null);
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
            if (input.Key is not { } key) return false;
            if (key.Key == ConsoleKey.Enter || editor.MatchesKeybinding("tui.select.confirm", key))
            {
                if (list.Selected is { } current) Toggle(current.Option.Key);
                list.ReplaceOptions(BuildOptions(), null);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.enableAll", key))
            {
                IEnumerable<string> targets = list.SearchQuery.Length == 0
                    ? allIds : list.VisibleOptions.Select(option => option.Key).ToArray();
                Enable(targets);
                list.ReplaceOptions(BuildOptions(), null);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.clearAll", key))
            {
                IEnumerable<string> targets = list.SearchQuery.Length == 0
                    ? allIds : list.VisibleOptions.Select(option => option.Key).ToArray();
                Clear(targets);
                list.ReplaceOptions(BuildOptions(), null);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.toggleProvider", key))
            {
                if (list.Selected is { } current)
                {
                    var provider = current.Option.Value.Provider;
                    var targets = models.Where(model => model.Provider!.Equals(provider, StringComparison.OrdinalIgnoreCase))
                        .Select(model => Key(provider!, model.Id)).ToArray();
                    if (targets.All(IsEnabled)) Clear(targets);
                    else Enable(targets);
                    list.ReplaceOptions(BuildOptions(), null);
                }
                return true;
            }
            if (editor.MatchesKeybinding("app.models.reorderUp", key))
            {
                Reorder(list.Selected?.Option.Key, -1);
                list.ReplaceOptions(BuildOptions(), null);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.reorderDown", key))
            {
                Reorder(list.Selected?.Option.Key, 1);
                list.ReplaceOptions(BuildOptions(), null);
                return true;
            }
            if (editor.MatchesKeybinding("app.models.save", key) || IsDefaultSaveKey(key))
            {
                var value = enabledIds is null ? null : JsonSerializer.Serialize(enabledIds);
                await persist(value).ConfigureAwait(false);
                ApplySessionScope();
                return true;
            }
            return false;
        }

        IReadOnlyList<TerminalSelectionOption<ModelDescriptor>> BuildOptions()
        {
            var orderedIds = enabledIds is null ? allIds.ToList() :
                enabledIds.Concat(allIds.Where(id => !enabledIds.Contains(id, StringComparer.OrdinalIgnoreCase))).ToList();
            return orderedIds.Select(id =>
            {
                var model = models.First(item => Key(item.Provider!, item.Id).Equals(id, StringComparison.OrdinalIgnoreCase));
                var enabled = IsEnabled(id);
                var provider = model.Provider!;
                var label = $"{(enabled ? "✓ " : "  ")}{model.Id} [{provider}]";
                var availability = model.Available ? model.Status ?? "available" : model.UnavailableReason ?? "unavailable";
                return new TerminalSelectionOption<ModelDescriptor>(id, model, label,
                    $"{model.Name ?? model.Id} · {availability}", $"{provider} {provider}/{model.Id} {model.Name}");
            }).ToArray();
        }

        List<string>? InitialEnabledIds(List<ModelDescriptor> available)
        {
            if (!modelRuntime.HasScope) return null;
            var result = ModelScopeResolver.Resolve(modelRuntime.Scope, available).Models
                .Where(item => item.Model.Provider is not null)
                .Select(item => Key(item.Model.Provider!, item.Model.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var pattern in modelRuntime.Scope)
            {
                if (pattern.Contains('/') && !pattern.Any(character => character is '*' or '?' or '[' or '{') &&
                    !result.Any(id => id.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                {
                    var slash = pattern.IndexOf('/');
                    var provider = pattern[..slash];
                    var id = pattern[(slash + 1)..];
                    if (provider.Length > 0 && id.Length > 0)
                    {
                        var unavailable = new ModelDescriptor(id, null, null, "unavailable", Provider: provider,
                            Available: false, UnavailableReason: "not present in the current catalogue");
                        available.Add(unavailable);
                        var key = Key(provider, id);
                        if (!allIds.Contains(key, StringComparer.OrdinalIgnoreCase)) allIds.Add(key);
                        result.Add(key);
                    }
                }
            }
            return result;
        }

        bool IsEnabled(string id) => enabledIds is null || enabledIds.Contains(id, StringComparer.OrdinalIgnoreCase);

        void Toggle(string id)
        {
            enabledIds ??= [.. allIds];
            var index = enabledIds.FindIndex(item => item.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) enabledIds.RemoveAt(index);
            else enabledIds.Add(id);
            if (enabledIds.Count == allIds.Count && allIds.All(item => enabledIds.Contains(item, StringComparer.OrdinalIgnoreCase)))
                enabledIds = null;
            ApplySessionScope();
        }

        void Enable(IEnumerable<string> targets)
        {
            if (enabledIds is null) return;
            foreach (var target in targets)
                if (!enabledIds.Contains(target, StringComparer.OrdinalIgnoreCase)) enabledIds.Add(target);
            if (allIds.All(item => enabledIds.Contains(item, StringComparer.OrdinalIgnoreCase))) enabledIds = null;
            ApplySessionScope();
        }

        void Clear(IEnumerable<string> targets)
        {
            enabledIds ??= [.. allIds];
            var remove = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
            enabledIds.RemoveAll(remove.Contains);
            ApplySessionScope();
        }

        void Reorder(string? id, int delta)
        {
            if (enabledIds is null || id is null) return;
            var index = enabledIds.FindIndex(item => item.Equals(id, StringComparison.OrdinalIgnoreCase));
            var next = index + delta;
            if (index < 0 || next < 0 || next >= enabledIds.Count) return;
            (enabledIds[index], enabledIds[next]) = (enabledIds[next], enabledIds[index]);
            ApplySessionScope();
        }

        void ApplySessionScope()
        {
            if (enabledIds is null || enabledIds.Count == 0) modelRuntime.ClearScope();
            else modelRuntime.SetScope(enabledIds);
        }
    }

    private static List<ModelDescriptor> NormalizeCatalog(IEnumerable<ModelDescriptor> catalog) => catalog
        .Select(model => model.Provider is null ? model with { Provider = "unknown" } : model)
        .GroupBy(model => Key(model.Provider!, model.Id), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First()).ToList();

    private static bool IsDefaultSaveKey(ConsoleKeyInfo key) =>
        key.Key == ConsoleKey.S && key.Modifiers.HasFlag(ConsoleModifiers.Control);

    private static string Key(string provider, string id) => $"{provider}/{id}";
}
