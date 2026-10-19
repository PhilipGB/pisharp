using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

public interface IPiSharpExtension
{
    void Configure(IPiSharpExtensionBuilder builder);
}

public interface IPiSharpExtensionBuilder
{
    void AddProvider(PiSharpExtensionProviderDefinition provider);
}

public static class PiSharpExtensionProviderContract
{
    public const string ChatApi = "pisharp-extension";
}

public sealed record PiSharpExtensionProviderModel(
    string Id,
    string? Name = null,
    int? ContextLength = null,
    int? MaxOutputTokens = null,
    IReadOnlyList<string>? Input = null,
    bool? Reasoning = null,
    JsonElement? ThinkingLevelMap = null);

public sealed record PiSharpExtensionProviderClientContext(
    string ProviderId,
    string ModelId,
    Uri? Endpoint,
    string? ApiKey);

public sealed record PiSharpExtensionProviderDefinition(
    string Id,
    string Name,
    IReadOnlyList<PiSharpExtensionProviderModel> Models,
    Func<PiSharpExtensionProviderClientContext, IChatClient> CreateChatClient)
{
    public Uri? Endpoint { get; init; }
    public string? ApiKeyEnvironment { get; init; }
    public bool AuthRequired { get; init; } = true;
}
