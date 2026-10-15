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
                _contexts[index].Unload();
                _contexts.RemoveAt(index);
            }
        }
    }
}
