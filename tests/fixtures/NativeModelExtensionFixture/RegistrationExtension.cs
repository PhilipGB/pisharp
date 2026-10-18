using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeModelFixture;

public sealed class RegistrationExtension : IPiSharpExtension
{
    private const string ProviderId = "native_fixture";
    private const string VirtualProviderId = "native_fixture_virtual";
#if NATIVE_MODEL_FIXTURE_v2
    private const string FixtureRevision = "v2";
    private const string MainModel = "physical-main-v2";
    private const string SecondaryModel = "physical-secondary-v2";
    private const string VirtualModelId = "auto-v2";
    private const string McpServerName = "native-fixture-mcp-v2";
    private const string McpToolName = "mcp__native_fixture_mcp_v2__echo_v2";
    private const string McpToolOperationName = "echo_v2";
#elif NATIVE_MODEL_FIXTURE_v3
    private const string FixtureRevision = "v3";
    private const string MainModel = "physical-main-v3";
    private const string SecondaryModel = "physical-secondary-v3";
    private const string VirtualModelId = "auto-v3";
    private const string McpServerName = "native-fixture-mcp-v3";
    private const string McpToolName = "mcp__native_fixture_mcp_v3__echo_v3";
    private const string McpToolOperationName = "echo_v3";
#else
    private const string FixtureRevision = "v1";
    private const string MainModel = "physical-main";
    private const string SecondaryModel = "physical-secondary";
    private const string VirtualModelId = "auto";
    private const string McpServerName = "native-fixture-mcp";
    private const string McpToolName = "mcp__native_fixture_mcp__echo";
    private const string McpToolOperationName = "echo";
#endif

    public void Configure(IPiSharpExtensionBuilder builder)
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(RegistrationExtension).Assembly.Location)!;
        builder.RegisterFlag("native-model-event-flag", PiSharpExtensionFlagType.Boolean,
            defaultValue: PiSharpExtensionFlagValue.FromBoolean(false));
        RegisterEventObservers(builder);
        builder.AddProvider(new PiSharpExtensionProviderDefinition(ProviderId, "Native fixture",
        [
            new(MainModel, "Native fixture " + FixtureRevision + " main", ContextLength: 32000, MaxOutputTokens: 2048,
                Input: ["text"], Reasoning: true),
            new(SecondaryModel, "Native fixture " + FixtureRevision + " secondary", ContextLength: 16000,
                MaxOutputTokens: 1024, Input: ["text"], Reasoning: true)
        ], context => new NativeFixtureChatClient(context.ModelId, McpToolName))
        {
            AuthRequired = false
        });
        builder.AddMcpServer(McpServerName, new PiSharpExtensionMcpServerConfiguration
        {
            Type = "stdio",
            Command = "python3",
            Arguments = [Path.Combine(assemblyDirectory, "native_mcp_fixture.py"), FixtureRevision,
                McpToolOperationName],
            WorkingDirectory = assemblyDirectory,
            TimeoutSeconds = 10,
            Exposure = "direct",
            Description = "Deterministic native extension MCP fixture."
        });
        builder.AddVirtualModel(new PiSharpExtensionVirtualModelDefinition(
            VirtualProviderId, VirtualModelId, "Native fixture " + FixtureRevision + " automatic model",
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previousCount = request.State is { ValueKind: JsonValueKind.Object } state &&
                    state.TryGetProperty("routeCount", out var count) ? count.GetInt32() : 0;
                RecordTrace($"route|{request.Reason}|{request.ThinkingLevel}|{previousCount}");
                return Task.FromResult(new PiSharpExtensionVirtualModelRoute(ProviderId, SecondaryModel,
                    request.ThinkingLevel, JsonSerializer.SerializeToElement(new { routeCount = previousCount + 1 })));
            })
        {
            ThinkingLevels = ["off", "low", "medium", "high", "xhigh"],
            ContextLength = 16000,
            MaxOutputTokens = 1024,
            Input = ["text"]
        });
        builder.AddCommand("native-model-fixture", (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("native model fixture " + FixtureRevision);
        }, "Report the native provider fixture revision.");
        builder.AddCommand("native-model-fixture-rename", async (name, command, cancellationToken) =>
        {
            await command.Session.SetSessionNameAsync(name, cancellationToken);
            return command.Session.SessionName ?? "<cleared>";
        }, "Rename the session through the public extension session context.");
        builder.AddCommand("native-model-fixture-tools", (_, command, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tools = command.GetConfiguredTools().Where(tool =>
                tool.Name.StartsWith("mcp__native_fixture_mcp", StringComparison.Ordinal)).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(tools));
        }, "Inspect the current fixture MCP tool metadata.");

#if NATIVE_MODEL_FIXTURE_v3
        throw new InvalidOperationException("native fixture Configure rollback probe");
#endif
    }

    private static void RegisterEventObservers(IPiSharpExtensionBuilder builder)
    {
        builder.AddSessionInfoChangedHandler(async (eventData, context, cancellationToken) =>
        {
            RecordTrace($"event|{FixtureRevision}|session-info|{eventData.Name ?? "<cleared>"}");
            if (context.Session is { } session)
                await session.AppendEntryAsync("native_event_observed",
                    JsonSerializer.SerializeToElement(new { kind = "session-info", name = eventData.Name }),
                    cancellationToken);
        });
        builder.AddSessionCompactHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|session-compact|{eventData.Reason}|{eventData.EntryId}|{eventData.WillRetry}");
            return ValueTask.CompletedTask;
        });
        builder.AddSessionCompactFailedHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|session-compact-failed|{eventData.Reason}|{eventData.Aborted}|{eventData.WillRetry}");
            return ValueTask.CompletedTask;
        });
        builder.AddModelSelectHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|model-select|{eventData.Source}|{eventData.PreviousModel?.ProviderId ?? "-"}/{eventData.PreviousModel?.ModelId ?? "-"}|{eventData.Model.ProviderId}/{eventData.Model.ModelId}");
            return ValueTask.CompletedTask;
        });
        builder.AddThinkingLevelSelectHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|thinking-select|{eventData.PreviousLevel}|{eventData.Level}");
            return ValueTask.CompletedTask;
        });
        builder.AddAgentStartHandler((_, context, _) =>
        {
            var capabilities = context.Capabilities ?? throw new InvalidOperationException(
                "The host did not provide event callback capabilities.");
            var session = capabilities.GetSessionSnapshot() ?? throw new InvalidOperationException(
                "The host did not provide the current session snapshot.");
            var model = capabilities.GetModel();
            var thinking = capabilities.GetThinkingLevel();
            var tools = string.Join(',', capabilities.GetActiveTools());
            var settings = capabilities.GetSettings();
            var flag = capabilities.GetFlag("native-model-event-flag")?.BooleanValue ?? false;
            RecordTrace($"event|{FixtureRevision}|agent-start|{context.Mode}|{session.SessionId}|{model?.ProviderId}/{model?.ModelId}|{thinking}|{tools}|{settings.ValueKind}|{flag}");
            return ValueTask.CompletedTask;
        });
        builder.AddAgentEndHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|agent-end|{eventData.Outcome}|{eventData.Messages.Count}");
            return ValueTask.CompletedTask;
        });
        builder.AddTurnStartHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|turn-start|{eventData.TurnIndex}");
            return ValueTask.CompletedTask;
        });
        builder.AddTurnEndHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|turn-end|{eventData.TurnIndex}|{eventData.MessageEntryId ?? "-"}|{eventData.ToolResultEntryIds.Count}|{eventData.Aborted}");
            return ValueTask.CompletedTask;
        });
        builder.AddToolExecutionStartHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|tool-start|{eventData.ToolCallId}|{eventData.ToolName}|{eventData.ParentToolCallId ?? "-"}");
            return ValueTask.CompletedTask;
        });
        builder.AddToolExecutionUpdateHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|tool-update|{eventData.ToolCallId}|{eventData.ToolName}");
            return ValueTask.CompletedTask;
        });
        builder.AddToolExecutionEndHandler((eventData, _, _) =>
        {
            RecordTrace($"event|{FixtureRevision}|tool-end|{eventData.ToolCallId}|{eventData.ToolName}|{eventData.IsError}|{eventData.DurationMs?.ToString() ?? "-"}");
            return ValueTask.CompletedTask;
        });
    }

    internal static void RecordTrace(string line)
    {
        var path = Environment.GetEnvironmentVariable("PISHARP_NATIVE_MODEL_TRACE");
        if (path is not null) File.AppendAllText(path, line + Environment.NewLine);
    }
}

internal sealed class NativeFixtureChatClient(string modelId, string mcpToolName) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var messageSnapshot = messages.ToArray();
        var tools = options?.Tools?.OfType<AIFunction>().ToArray() ?? [];
        var hasSystemPrompt = !string.IsNullOrWhiteSpace(options?.Instructions) ||
            messageSnapshot.Any(message => message.Role == ChatRole.System);
        var hasToolResult = messageSnapshot.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>().Any();
        RegistrationExtension.RecordTrace($"request|{modelId}|{string.Join(',', tools.Select(tool => tool.Name))}|" +
            $"tool-result:{hasToolResult.ToString().ToLowerInvariant()}|system:{hasSystemPrompt.ToString().ToLowerInvariant()}");
        var mcpTool = tools.FirstOrDefault(tool => tool.Name == mcpToolName);
        if (modelId == MainModel && !hasToolResult && mcpTool is not null)
        {
            var functionCall = new FunctionCallContent("native-fixture-mcp-call", mcpTool.Name,
                new Dictionary<string, object?> { ["value"] = "native-fixture-mcp-value" });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [functionCall])
            {
                MessageId = "native-fixture-call-message"
            }) { ModelId = modelId });
        }
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            "native-fixture-provider-response") { MessageId = "native-fixture-response" }) { ModelId = modelId });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

#if NATIVE_MODEL_FIXTURE_v2
    private const string MainModel = "physical-main-v2";
#else
    private const string MainModel = "physical-main";
#endif
}
