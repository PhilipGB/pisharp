using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Runtime.VirtualModels;

public static class VirtualModelContract
{
    public const string Api = "pi-virtual";
    public const string StateEntry = "pi.virtual-model-state";
}

public sealed record VirtualModelRouteRequest(ModelDescriptor Model, string ThinkingLevel, string Reason,
    IReadOnlyList<ChatMessage> Messages, ModelDescriptor? PreviousModel = null,
    string? PreviousThinkingLevel = null, ModelDescriptor? FailedModel = null,
    string? FailedThinkingLevel = null, ChatMessage? FailedMessage = null, JsonElement? State = null);

public sealed record VirtualModelRoute(string Provider, string Model, string ThinkingLevel, JsonElement? State = null);

public delegate Task<VirtualModelRoute> VirtualModelRouter(VirtualModelRouteRequest request,
    CancellationToken cancellationToken);

public sealed record VirtualModelDefinition(string Provider, string Id, string Name, VirtualModelRouter Route,
    IReadOnlyList<string>? ThinkingLevels = null, int? ContextLength = null, int? MaxOutputTokens = null,
    IReadOnlyList<string>? Input = null);

public sealed record RegisteredVirtualModel(VirtualModelDefinition Definition, ModelDescriptor Model, string ExtensionPath);

/// <summary>Catalog and routing contracts for extension-registered logical models.</summary>
public sealed class VirtualModelRegistry
{
    private static readonly string[] s_thinkingLevels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    private readonly object _gate = new();
    private readonly Dictionary<(string Provider, string Id), RegisteredVirtualModel> _models = [];

    public IReadOnlyCollection<RegisteredVirtualModel> Models
    {
        get { lock (_gate) return _models.Values.ToArray(); }
    }

    public void Register(VirtualModelDefinition definition, string extensionPath)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(definition.Route);
        if (string.IsNullOrWhiteSpace(definition.Provider) || string.IsNullOrWhiteSpace(definition.Id))
            throw new ArgumentException("Virtual model provider and id cannot be empty.", nameof(definition));
        var levels = definition.ThinkingLevels ?? ["off"];
        if (levels.Count == 0 || levels.Any(level => !s_thinkingLevels.Contains(level, StringComparer.Ordinal)))
            throw new ArgumentException("Virtual model thinking levels must contain supported levels.", nameof(definition));
        var descriptor = new ModelDescriptor(definition.Id, null,
            definition.ContextLength is > 0 ? definition.ContextLength : null, "virtual model",
            Reasoning: levels.Any(level => level != "off"), Provider: definition.Provider,
            Name: string.IsNullOrWhiteSpace(definition.Name) ? definition.Id : definition.Name,
            MaxOutputTokens: definition.MaxOutputTokens is > 0 ? definition.MaxOutputTokens : null,
            Input: definition.Input ?? ["text", "image"], Api: VirtualModelContract.Api);
        var key = (definition.Provider, definition.Id);
        lock (_gate)
        {
            if (_models.TryGetValue(key, out var existing) && existing.ExtensionPath != extensionPath)
                throw new InvalidOperationException($"Virtual model {definition.Provider}/{definition.Id} is already registered by {existing.ExtensionPath}.");
            _models[key] = new(definition, descriptor, extensionPath);
        }
    }

    public void Unregister(string provider, string id, string extensionPath)
    {
        lock (_gate)
            if (_models.TryGetValue((provider, id), out var current) && current.ExtensionPath == extensionPath)
                _models.Remove((provider, id));
    }

    public RegisteredVirtualModel? Get(string provider, string id)
    {
        lock (_gate) return _models.GetValueOrDefault((provider, id));
    }

    public IReadOnlyList<ModelDescriptor> List(string? provider = null)
    {
        lock (_gate) return _models.Values.Where(item => provider is null || item.Model.Provider == provider)
            .Select(item => item.Model).OrderBy(item => item.Provider, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }

    public Task<VirtualModelRoute> RouteAsync(RegisteredVirtualModel model, VirtualModelRouteRequest request,
        CancellationToken cancellationToken) => model.Definition.Route(request, cancellationToken);
}
