using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>Provider identity used for chat models implemented by a native extension.</summary>
public static class PiSharpExtensionProviderContract
{
    /// <summary>The provider API recorded for requests handled by an extension-owned chat client.</summary>
    public const string ChatApi = "pisharp-extension";
}

/// <summary>Model metadata contributed by a compiled extension provider.</summary>
public sealed record PiSharpExtensionProviderModel(
    string Id,
    string? Name = null,
    int? ContextLength = null,
    int? MaxOutputTokens = null,
    IReadOnlyList<string>? Input = null,
    bool? Reasoning = null,
    JsonElement? ThinkingLevelMap = null);

/// <summary>The minimal resolved connection data supplied when a provider chat client is created.</summary>
public sealed record PiSharpExtensionProviderClientContext(
    string ProviderId,
    string ModelId,
    Uri? Endpoint,
    string? ApiKey);

/// <summary>A provider and its models, backed by a standard MEAI chat client.</summary>
public sealed record PiSharpExtensionProviderDefinition(
    string Id,
    string Name,
    IReadOnlyList<PiSharpExtensionProviderModel> Models,
    Func<PiSharpExtensionProviderClientContext, IChatClient> CreateChatClient)
{
    /// <summary>Optional absolute HTTP endpoint supplied to the client factory.</summary>
    public Uri? Endpoint { get; init; }

    /// <summary>Environment variable resolved by PiSharp's credential owner.</summary>
    public string? ApiKeyEnvironment { get; init; }

    /// <summary>Whether normal model selection requires a resolved API key.</summary>
    public bool AuthRequired { get; init; } = true;
}

/// <summary>Supported MCP transports exposed to compiled extensions.</summary>
public sealed record PiSharpExtensionMcpServerConfiguration
{
    /// <summary>Transport name: stdio, http, or streamable-http. When omitted, it is inferred from command or URL.</summary>
    public string? Type { get; init; }

    /// <summary>Executable for a stdio server.</summary>
    public string? Command { get; init; }

    /// <summary>Arguments passed to a stdio server.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory for a stdio server.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment passed to a stdio server.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Absolute HTTP or streamable HTTP endpoint.</summary>
    public Uri? Url { get; init; }

    /// <summary>Headers sent to an HTTP MCP server.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional OAuth configuration supported by the normal MCP runtime.</summary>
    public JsonElement? OAuth { get; init; }

    /// <summary>Optional provider whose stored credential supplies HTTP bearer authentication.</summary>
    public string? AuthProvider { get; init; }

    /// <summary>Whether the server connects during ordinary PiSharp startup.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Default exposure for tools discovered from this server.</summary>
    public string? Exposure { get; init; }

    /// <summary>Per-tool exposure overrides.</summary>
    public IReadOnlyDictionary<string, string> ToolExposure { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Connection timeout in seconds.</summary>
    public double? TimeoutSeconds { get; init; }

    /// <summary>Optional server description shown by existing MCP surfaces.</summary>
    public string? Description { get; init; }
}

/// <summary>Safe model metadata visible to a virtual-model route callback.</summary>
public sealed record PiSharpExtensionModelIdentity(
    string Provider,
    string Id,
    string? Name,
    int? ContextLength,
    int? MaxOutputTokens,
    IReadOnlyList<string>? Input,
    bool? Reasoning,
    string? Api);

/// <summary>Request data supplied to a virtual-model route callback.</summary>
public sealed record PiSharpExtensionVirtualModelRouteRequest(
    PiSharpExtensionModelIdentity Model,
    string ThinkingLevel,
    string Reason,
    IReadOnlyList<ChatMessage> Messages,
    PiSharpExtensionModelIdentity? PreviousModel = null,
    string? PreviousThinkingLevel = null,
    PiSharpExtensionModelIdentity? FailedModel = null,
    string? FailedThinkingLevel = null,
    ChatMessage? FailedMessage = null,
    string? FailedError = null,
    JsonElement? State = null);

/// <summary>A physical provider model and thinking level selected for one routed request.</summary>
public sealed record PiSharpExtensionVirtualModelRoute(
    string Provider,
    string Model,
    string ThinkingLevel,
    JsonElement? State = null);

/// <summary>Routes a virtual-model request using immutable request data and persisted JSON state.</summary>
public delegate Task<PiSharpExtensionVirtualModelRoute> PiSharpExtensionVirtualModelRouteHandler(
    PiSharpExtensionVirtualModelRouteRequest request,
    CancellationToken cancellationToken);

/// <summary>A selectable logical model whose route callback chooses a physical provider model per request.</summary>
public sealed record PiSharpExtensionVirtualModelDefinition(
    string Provider,
    string Id,
    string Name,
    PiSharpExtensionVirtualModelRouteHandler Route)
{
    /// <summary>Thinking levels accepted by the logical model.</summary>
    public IReadOnlyList<string>? ThinkingLevels { get; init; }

    /// <summary>Context length shown before the first physical response is available.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Maximum output tokens shown before routing.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Supported input types, such as text and image.</summary>
    public IReadOnlyList<string>? Input { get; init; }
}
