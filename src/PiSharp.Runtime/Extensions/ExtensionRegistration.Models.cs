using System.Text.Json;
using System.Text.RegularExpressions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed record RegisteredExtensionProvider(
    NativeExtensions.PiSharpExtensionProviderDefinition Definition,
    string ExtensionPath);

public sealed partial class ExtensionRegistration
{
    private static readonly Regex s_providerId = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex s_environmentName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private readonly object _providerGate = new();
    private readonly Dictionary<string, RegisteredExtensionProvider> _extensionProviders = new(StringComparer.Ordinal);

    internal IReadOnlyCollection<RegisteredExtensionProvider> ExtensionProviders
    {
        get { lock (_providerGate) return _extensionProviders.Values.ToArray(); }
    }

    internal void RegisterProvider(NativeExtensions.PiSharpExtensionProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var source = _currentSourceInfo ?? throw new InvalidOperationException(
            "Providers can only be registered while an extension is being configured.");
        ValidateProvider(definition, source.Path);
        var snapshot = definition with
        {
            Models = definition.Models.Select(model => model with
            {
                Input = model.Input?.ToArray(),
                ThinkingLevelMap = model.ThinkingLevelMap?.Clone()
            }).ToArray()
        };
        lock (_providerGate)
        {
            if (_extensionProviders.TryGetValue(snapshot.Id, out var existing))
                throw new InvalidOperationException($"Provider '{snapshot.Id}' is already registered by extension " +
                    $"'{existing.ExtensionPath}'; conflicting extension '{source.Path}'.");
            _extensionProviders.Add(snapshot.Id, new(snapshot, source.Path));
        }
    }

    internal void ClearExtensionProviders()
    {
        lock (_providerGate) _extensionProviders.Clear();
    }

    internal Action CreateExtensionProviderCheckpoint()
    {
        lock (_providerGate)
        {
            var saved = new Dictionary<string, RegisteredExtensionProvider>(_extensionProviders, StringComparer.Ordinal);
            return () =>
            {
                lock (_providerGate)
                {
                    _extensionProviders.Clear();
                    foreach (var pair in saved) _extensionProviders.Add(pair.Key, pair.Value);
                }
            };
        }
    }

    private static void ValidateProvider(NativeExtensions.PiSharpExtensionProviderDefinition provider,
        string extensionPath)
    {
        if (!s_providerId.IsMatch(provider.Id))
            throw new ArgumentException($"Invalid provider id '{provider.Id}'. Use lowercase ASCII letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(provider.Name))
            throw new ArgumentException($"Provider '{provider.Id}' must have a display name.");
        ArgumentNullException.ThrowIfNull(provider.Models);
        ArgumentNullException.ThrowIfNull(provider.CreateChatClient);
        if (provider.Models.Count == 0)
            throw new ArgumentException($"Provider '{provider.Id}' must register at least one chat model.");
        if (provider.Endpoint is { } endpoint && (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
            endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0))
            throw new ArgumentException($"Provider '{provider.Id}' endpoint must be an absolute HTTP or HTTPS URI without user information or a fragment.");
        if (provider.ApiKeyEnvironment is { } environmentName && !s_environmentName.IsMatch(environmentName))
            throw new ArgumentException($"Provider '{provider.Id}' has an invalid API-key environment variable name.");

        var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in provider.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 256 || model.Id.Any(char.IsControl))
                throw new ArgumentException($"Provider '{provider.Id}' has an invalid model id.");
            if (!modelIds.Add(model.Id))
                throw new InvalidOperationException($"Provider '{provider.Id}' from extension '{extensionPath}' " +
                    $"has duplicate model id '{model.Id}'.");
            if (string.IsNullOrWhiteSpace(model.Name) && model.Name is not null)
                throw new ArgumentException($"Model '{provider.Id}/{model.Id}' has an invalid display name.");
            if (model.ContextLength is <= 0 || model.MaxOutputTokens is <= 0)
                throw new ArgumentException($"Model '{provider.Id}/{model.Id}' token limits must be positive when specified.");
            if (model.Input?.Any(input => input is not ("text" or "image")) == true)
                throw new ArgumentException($"Model '{provider.Id}/{model.Id}' supports an input type that PiSharp does not model.");
            if (model.ThinkingLevelMap is { ValueKind: not JsonValueKind.Object })
                throw new ArgumentException($"Model '{provider.Id}/{model.Id}' thinking-level metadata must be a JSON object.");
        }
    }
}
