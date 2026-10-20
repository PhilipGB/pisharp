using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

public sealed partial class ExtensionCatalog
{
    private void SelectPreloadedExtensions(IReadOnlyList<(string Path, ResourceSourceInfo SourceInfo)> selected)
    {
        var sources = selected.ToDictionary(entry => ResourcePathIdentity.Canonicalize(entry.Path),
            entry => entry.SourceInfo, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var index = _loadedExtensions.Count - 1; index >= 0; index--)
        {
            var previous = _loadedExtensions[index];
            var next = sources.GetValueOrDefault(ResourcePathIdentity.Canonicalize(previous.Path));
            if (next is not null) next = next with { Path = previous.Path };
            Registration.ReconcileExtension(previous, next);
            if (next is not null) _loadedExtensions[index] = next;
            else
            {
                _loadedExtensions.RemoveAt(index);
                Registration.DisposePackageUiScope(previous.Path);
                if (_nativePackageHandles[index] is { } package) package.Unload();
                else _contexts[index].Unload();
                _contexts.RemoveAt(index);
                _nativePackageHandles.RemoveAt(index);
            }
        }
    }

    private void OrderSelectedExtensions(IReadOnlyList<(string Path, ResourceSourceInfo SourceInfo)> selected)
    {
        var ranks = selected.Select((entry, index) => (Path: ResourcePathIdentity.Canonicalize(entry.Path), Rank: index))
            .ToDictionary(entry => entry.Path, entry => entry.Rank,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var ordered = _loadedExtensions.Select((source, index) =>
                (Source: source, Context: _contexts[index], Package: _nativePackageHandles[index]))
            .OrderBy(entry => ranks[ResourcePathIdentity.Canonicalize(entry.Source.Path)]).ToArray();
        _loadedExtensions.Clear();
        _loadedExtensions.AddRange(ordered.Select(entry => entry.Source));
        _contexts.Clear();
        _contexts.AddRange(ordered.Select(entry => entry.Context));
        _nativePackageHandles.Clear();
        _nativePackageHandles.AddRange(ordered.Select(entry => entry.Package));
        Registration.OrderExtensions(_loadedExtensions);
    }
}
