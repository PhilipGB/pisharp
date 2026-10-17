using System.Text.Json;
using NativeExtensions = PiSharp.Extensions.Abstractions;
using RuntimeVirtualModel = PiSharp.Runtime.VirtualModels.VirtualModelDefinition;
using RuntimeVirtualRoute = PiSharp.Runtime.VirtualModels.VirtualModelRoute;
using RuntimeVirtualRouteRequest = PiSharp.Runtime.VirtualModels.VirtualModelRouteRequest;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddProvider(NativeExtensions.PiSharpExtensionProviderDefinition provider)
    {
        EnsureConfiguring();
        registration.RegisterProvider(provider);
    }

    public void AddMcpServer(string name, NativeExtensions.PiSharpExtensionMcpServerConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureConfiguring();
        registration.RegisterMcpServer(name, ToMcpConfiguration(configuration));
    }

    public void AddVirtualModel(NativeExtensions.PiSharpExtensionVirtualModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(model.Route);
        EnsureConfiguring();
        registration.RegisterVirtualModel(new RuntimeVirtualModel(model.Provider, model.Id, model.Name,
            async (request, cancellationToken) =>
            {
                var publicRequest = new NativeExtensions.PiSharpExtensionVirtualModelRouteRequest(
                    ToPublicModel(request.Model), request.ThinkingLevel, request.Reason,
                    Array.AsReadOnly(request.Messages.Select(ClonePublicMessage).ToArray()),
                    request.PreviousModel is null ? null : ToPublicModel(request.PreviousModel),
                    request.PreviousThinkingLevel,
                    request.FailedModel is null ? null : ToPublicModel(request.FailedModel),
                    request.FailedThinkingLevel, request.FailedMessage is null ? null :
                        ClonePublicMessage(request.FailedMessage), request.FailedError,
                    request.State?.Clone());
                var route = await model.Route(publicRequest, cancellationToken).ConfigureAwait(false);
                return new RuntimeVirtualRoute(route.Provider, route.Model, route.ThinkingLevel,
                    route.State?.Clone());
            }, model.ThinkingLevels?.ToArray(), model.ContextLength, model.MaxOutputTokens,
            model.Input?.ToArray()));
    }

    private static JsonElement ToMcpConfiguration(NativeExtensions.PiSharpExtensionMcpServerConfiguration configuration)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["args"] = (configuration.Arguments ?? []).ToArray(),
            ["env"] = (configuration.Environment ?? new Dictionary<string, string>())
                .ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal),
            ["headers"] = (configuration.Headers ?? new Dictionary<string, string>())
                .ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            ["enabled"] = configuration.Enabled,
            ["toolExposure"] = (configuration.ToolExposure ?? new Dictionary<string, string>())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        if (configuration.Type is not null) values["type"] = configuration.Type;
        if (configuration.Command is not null) values["command"] = configuration.Command;
        if (configuration.WorkingDirectory is not null) values["cwd"] = configuration.WorkingDirectory;
        if (configuration.Url is not null) values["url"] = configuration.Url.ToString();
        if (configuration.Exposure is not null) values["exposure"] = configuration.Exposure;
        if (configuration.TimeoutSeconds is not null) values["timeout"] = configuration.TimeoutSeconds;
        if (configuration.Description is not null) values["description"] = configuration.Description;
        if (configuration.OAuth is { } oauth) values["oauth"] = oauth.Clone();
        if (configuration.AuthProvider is not null)
            values["auth"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = configuration.AuthProvider };
        return JsonSerializer.SerializeToElement(values);
    }

    private static NativeExtensions.PiSharpExtensionModelIdentity ToPublicModel(
        PiSharp.Runtime.Providers.ModelDescriptor model) => new(model.Provider ?? "", model.Id,
            model.Name, model.ContextLength, model.MaxOutputTokens, model.Input?.ToArray(), model.Reasoning, model.Api);

    private static Microsoft.Extensions.AI.ChatMessage ClonePublicMessage(Microsoft.Extensions.AI.ChatMessage message)
    {
        var clone = PiSharpContextTransformPipeline.CloneMessage(message);
        clone.RawRepresentation = null;
        foreach (var content in clone.Contents) content.RawRepresentation = null;
        return clone;
    }
}
