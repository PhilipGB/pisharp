using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Retains the interactive available catalog and provider count under one publication lock.</summary>
internal sealed class AvailableModelSnapshot
{
    private readonly object _availableProviderGate = new();
    private readonly Dictionary<string, bool> _availableProviderSnapshot = new(StringComparer.Ordinal);
    private ModelDescriptor[] _availableModelSnapshot = [];

    public AvailableModelSnapshot(IEnumerable<ProviderProfile> authenticatedProviders)
    {
        foreach (var provider in authenticatedProviders)
        {
            _availableProviderSnapshot[provider.Id] = true;
            _availableModelSnapshot = _availableModelSnapshot.Concat(provider.Models.Where(model => model.Available)
                    .Select(model => model with { Provider = provider.Id }))
                .GroupBy(ModelKey, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        }
    }

    public int ProviderCount
    {
        get { lock (_availableProviderGate) return _availableProviderSnapshot.Count(item => item.Value); }
    }

    public IReadOnlyList<ModelDescriptor> Models
    {
        get { lock (_availableProviderGate) return _availableModelSnapshot.ToArray(); }
    }

    public void RecordProvider(string providerId, bool available)
    {
        lock (_availableProviderGate) _availableProviderSnapshot[providerId] = available;
    }

    public void RecordModels(IEnumerable<ModelDescriptor> models)
    {
        lock (_availableProviderGate)
            _availableModelSnapshot = models.Where(model => model.Available)
                .GroupBy(ModelKey, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
    }

    public void RecordModels(string providerId, IEnumerable<ModelDescriptor> models)
    {
        lock (_availableProviderGate)
        {
            var retained = _availableModelSnapshot.Where(model => !string.Equals(model.Provider, providerId,
                StringComparison.Ordinal));
            _availableModelSnapshot = retained.Concat(models.Where(model => model.Available))
                .GroupBy(ModelKey, StringComparer.Ordinal).Select(group => group.Last()).ToArray();
        }
    }

    public void RecordModel(ModelDescriptor model)
    {
        lock (_availableProviderGate)
            _availableModelSnapshot = _availableModelSnapshot.Where(existing =>
                    !string.Equals(ModelKey(existing), ModelKey(model), StringComparison.Ordinal))
                .Append(model).ToArray();
    }

    private static string ModelKey(ModelDescriptor model) => $"{model.Provider}\0{model.Id}";

}
